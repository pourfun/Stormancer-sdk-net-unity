﻿using Stormancer.Core;
using Stormancer.Networking.Messages;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Stormancer.Plugins
{
    class RpcClientPlugin : IClientPlugin
    {
        internal const string NextRouteName = "stormancer.rpc.next";
        internal const string ErrorRouteName = "stormancer.rpc.error";
        internal const string CompletedRouteName = "stormancer.rpc.completed";
        internal const string Version = "1.0.0";
        internal const string PluginName = "stormancer.plugins.rpc";
        public void Build(PluginBuildContext ctx)
        {
            ctx.SceneCreated += scene =>
            {
                var rpcParams = scene.GetHostMetadata(PluginName);

                if (rpcParams == Version)
                {
                    var processor = new RpcService(scene);
                    scene.RegisterComponent(() => processor);
                    scene.AddRoute(NextRouteName, p =>
                    {
                        processor.Next(p);
                    });
                    scene.AddRoute(ErrorRouteName, p =>
                    {
                        processor.Error(p);
                    });
                    scene.AddRoute(CompletedRouteName, p =>
                    {
                        processor.Complete(p);
                    });

                }
            };
        }

        /// <summary>
        /// Used to send remote procedure call through the RPC plugin
        /// </summary>
        /// <remarks>
        /// If your scene uses the RPC plugin, use `scene.GetService&lt;RpcRequestProcessor&gt;()` to get an instance of this class.
        /// You can also use the `Scene.SendRequest` extension methods.
        /// </remarks>
        public class RpcService
        {

            private ushort _currentRequestId = 0;
            private class Request
            {
                public IObserver<Packet<IScenePeer>> Observer { get; set; }
                public int ReceivedMsg;
                public TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
            }
            private readonly object _lock = new object();
            private readonly ConcurrentDictionary<ushort, Request> _pendingRequests = new ConcurrentDictionary<ushort, Request>();
            private readonly Scene _scene;

            internal RpcService(Scene scene)
            {
                _scene = scene;
            }
            /// <summary>
            /// Starts a RPC to the scene host.
            /// </summary>
            /// <param name="route">The remote route on which the message will be sent.</param>
            /// <param name="writer">The writer used to build the request's content.</param>
            /// <param name="priority">The priority used to send the request.</param>
            /// <returns>An IObservable instance returning the RPC responses.</returns>
            public IObservable<Packet<IScenePeer>> Rpc(string route, Action<Stream> writer, PacketPriority priority)
            {
                return Observable.Create<Packet<IScenePeer>>(
                    observer =>
                    {
                        var rr = _scene.RemoteRoutes.FirstOrDefault(r => r.Name == route);
                        if (rr == null)
                        {
                            throw new ArgumentException("The target route does not exist on the remote host.");
                        }
                        string version;
                        if (!rr.Metadata.TryGetValue(RpcClientPlugin.PluginName, out version) || version != RpcClientPlugin.Version)
                        {
                            throw new InvalidOperationException("The target remote route does not support the plugin RPC version " + Version);
                        }

                        var rq = new Request { Observer = observer };
                        var id = this.ReserveId();
                        if (_pendingRequests.TryAdd(id, rq))
                        {

                            _scene.SendPacket(route, s =>
                            {
                                s.Write(BitConverter.GetBytes(id), 0, 2);
                                writer(s);
                            }, priority, PacketReliability.RELIABLE_ORDERED);
                        }

                        return () =>
                        {
                            _pendingRequests.TryRemove(id, out rq);

                        };
                    });
            }

            /// <summary>
            /// Number of pending RPCs.
            /// </summary>
            public ushort PendingRequests
            {
                get
                {
                    return (ushort)_pendingRequests.Count;
                }
            }

            /// <summary>
            /// Adds a procedure that can be called by remote peer to the scene.
            /// </summary>
            /// <param name="route"></param>
            /// <param name="handler"></param>
            /// <param name="ordered">True if the message should be alwayse receive in order, false otherwise.</param>
            /// <remarks>
            /// The procedure is added to the scene to which this service is attached.
            /// </remarks>
            public void AddProcedure(string route, Func<RequestContext<IScenePeer>, Task> handler, bool ordered)
            {
                this._scene.AddRoute(route, p =>
                {
                    var buffer = new byte[2];
                    p.Stream.Read(buffer, 0, 2);
                    var id = BitConverter.ToUInt16(buffer, 0);

                    var ctx = new RequestContext<IScenePeer>(p.Connection, _scene, id, ordered);

                    handler(ctx).ContinueWith(t =>
                    {
                        if (t.IsCompleted)
                        {
                            ctx.SendCompleted();
                        }
                        else
                        {
                            var ex = t.Exception.InnerExceptions.OfType<ClientException>();
                            if (ex.Any())
                            {
                                ctx.SendError(string.Join("|", ex.Select(e => e.Message)));
                            }
                        }

                    });
                }, new Dictionary<string, string> { { "stormancer.plugins.rpc", "1.0.0" } });
            }
            private ushort ReserveId()
            {
                lock (this._lock)
                {
                    unchecked
                    {
                        int loop = 0;
                        while (_pendingRequests.ContainsKey(_currentRequestId))
                        {
                            loop++;
                            _currentRequestId++;
                            if (loop > ushort.MaxValue)
                            {
                                throw new InvalidOperationException("Too many requests in progress, unable to start a new one.");
                            }
                        }
                        return _currentRequestId;
                    }
                }
            }

            private Request GetPendingRequest(Packet<IScenePeer> p)
            {
                var buffer = new byte[2];
                p.Stream.Read(buffer, 0, 2);
                var id = BitConverter.ToUInt16(buffer, 0);

                Request observer;
                if (_pendingRequests.TryGetValue(id, out observer))
                {
                    return observer;
                }
                else
                {
                    return null;
                }
            }
            internal void Next(Packet<IScenePeer> p)
            {
                var rq = GetPendingRequest(p);
                if (rq != null)
                {
                    System.Threading.Interlocked.Increment(ref rq.ReceivedMsg);
                    rq.Observer.OnNext(p);
                    if (!rq.tcs.Task.IsCompleted)
                    {
                        rq.tcs.TrySetResult(true);
                    }
                }
            }

            internal void Error(Packet<IScenePeer> p)
            {
                var rq = GetPendingRequest(p);
                if (rq != null)
                {
                    rq.Observer.OnError(new ClientException(p.ReadObject<string>()));
                }
            }

            internal void Complete(Packet<IScenePeer> p)
            {
                var messageSent = p.Stream.ReadByte() != 0;
                var rq = GetPendingRequest(p);
                if (rq != null)
                {
                    if (messageSent)
                    {
                        rq.tcs.Task.ContinueWith(t => {
                            rq.Observer.OnCompleted();
                        });
                    }
                    else
                    {
                        rq.Observer.OnCompleted();
                    }

                }
            }
        }
    }


}
