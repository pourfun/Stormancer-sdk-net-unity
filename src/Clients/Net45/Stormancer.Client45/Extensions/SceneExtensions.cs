﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reactive.Linq;
using Stormancer.Core;
using System.Reactive;
using System.IO;
namespace Stormancer
{
    /// <summary>
    /// Extensions for the Scene class.
    /// </summary>
    public static class SceneExtensions
    {
        /// <summary>
        /// Listen to messages on the specified route, and output instances of T using the scene serializer.
        /// </summary>
        /// <typeparam name="T">Type into which message contents should be deserialized.</typeparam>
        /// <param name="scene">The scene to listen to.</param>
        /// <param name="route">A string describing the route to listen to.</param>
        /// <returns>An observable instance providing the messages.</returns>
        public static IObservable<T> OnMessage<T>(this Scene scene, string route)
        {


            return scene.OnMessage(route).Select(packet =>
            {
                var value = packet.Serializer().Deserialize<T>(packet.Stream);

                return value;
            });
        }


       
        /// <summary>
        /// Sends a request to a scene without expecting a response
        /// </summary>
        /// <param name="scene">The scene on which the message will be sent.</param>
        /// <param name="route">A string containing the name of the route used to send the request.</param>
        /// <param name="priority"></param>
        /// <returns>A task completing with the request.</returns>
        /// <remarks>
        /// Uses the RPC plugin
        /// </remarks>
        public static async Task SendVoidRequest(this Scene scene, string route, PacketPriority priority = PacketPriority.MEDIUM_PRIORITY)
        {
            await scene.Rpc(route, s =>
            {

            }, priority).FirstOrDefaultAsync();
        }

        /// <summary>
        /// Sends a request to a scene that doesn't return data.
        /// </summary>
        /// <param name="scene">The target scene.</param>
        /// <param name="route">The name of the route used to send the request.</param>
        /// <param name="parameter">A parameter object that will be sent as content of the request.</param>
        /// <param name="priority">An optional priority level for the request.</param>
        /// <returns>A task completing with the request.</returns>
        /// <remarks>Uses the RPC plugin</remarks>
        public static async Task SendVoidRequest(this Scene scene, string route, object parameter, PacketPriority priority = PacketPriority.MEDIUM_PRIORITY)
        {
            await scene.Rpc(route, s =>
            {
                scene.Host.Serializer().Serialize(parameter, s);
            }, priority).FirstOrDefaultAsync();
        }
        /// <summary>
        /// Sends a RPC to a scene host.
        /// </summary>
        /// <typeparam name="TOut">The type of the data returned by the request.</typeparam>
        /// <param name="scene">The target scene.</param>
        /// <param name="route">The target route.</param>
        /// <param name="priority">The request priority. Only applies to the request, not the response.</param>
        /// <param name="parameter">The request parameter</param>
        /// <returns>A task completing with the request.</returns>
        public static IObservable<TOut> SendRequest<TOut>(this Scene scene, string route, object parameter, PacketPriority priority = PacketPriority.MEDIUM_PRIORITY)
        {
            return scene.Rpc(route, s =>
            {
                scene.Host.Serializer().Serialize(parameter, s);
            }, priority).Select(packet =>
            {
                var value = packet.Serializer().Deserialize<TOut>(packet.Stream);

                return value;
            });
        }

        /// <summary>
        /// Sends a remote procedure call using raw binary data as input and output.
        /// </summary>
        /// <param name="scene">The target scene. </param>
        /// <param name="route">The target route</param>
        /// <param name="writer">A writer method writing</param>
        /// <param name="priority">The priority level used to send the request.</param>
        /// <returns>An IObservable instance that provides return values for the request.</returns>
        public static IObservable<Packet<IScenePeer>> Rpc(this Scene scene, string route, Action<Stream> writer, PacketPriority priority = PacketPriority.MEDIUM_PRIORITY)
        {
            var rpcService = scene.GetComponent<Stormancer.Plugins.RpcClientPlugin.RpcService>();
            if (rpcService == null)
            {
                throw new NotSupportedException("RPC plugin not available.");
            }
            return rpcService.Rpc(route, writer, priority);
        }
        /// <summary>
        /// Sends an object to the target scene with the requested reliability and priority levels.
        /// </summary>
        /// <typeparam name="T">The type of the parameter object.</typeparam>
        /// <param name="scene">The target scene.</param>
        /// <param name="route">The target route on the scene.</param>
        /// <param name="data">The data that will be serialized then sent.</param>
        /// <param name="priority">The priority level.</param>
        /// <param name="reliability">The reliability level</param>
        public static void Send<T>(this Scene scene, string route, T data, PacketPriority priority = PacketPriority.HIGH_PRIORITY, PacketReliability reliability = PacketReliability.RELIABLE_ORDERED)
        {
            scene.Host.Send(route, s =>
            {
                scene.Host.Serializer().Serialize(data, s);
            }, priority, reliability);
        }

        /// <summary>
        /// Listen to messages on the specified route, deserialize them and execute the given handler for eah of them.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="scene">The remote scene proxy on which the route messages will be listened.</param>
        /// <param name="route">The route to listen.</param>
        /// <param name="handler">The handler to execute for each message on the route.</param>
        /// <returns>An IDisposable object you can use to unregister the handler.</returns>
        public static IDisposable AddRoute<T>(this Scene scene, string route, Action<T> handler)
        {
            return scene.OnMessage<T>(route).Subscribe(handler);
        }

        /// <summary>
        /// Listen to messages on the specified route, deserialize them and execute the given handler for eah of them. (Duplicate of AddRoute for compatibility)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="scene">The remote scene proxy on which the route messages will be listened.</param>
        /// <param name="route">The route to listen.</param>
        /// <param name="handler">The handler to execute for each message on the route.</param>
        /// <returns>An IDisposable object you can use to unregister the handler.</returns>
        /// <remarks>RegisterRoute is an alias to the AddRoute method.</remarks>
        public static IDisposable RegisterRoute<T>(this Scene scene, string route, Action<T> handler)
        {
            return scene.AddRoute(route, handler);
        }

        /// <summary>
        /// Adds a procedure to the scene.
        /// </summary>
        /// <remarks>
        /// Procedures provide an asynchronous request/response pattern on top of scenes using the RPC plugin. 
        /// Procedures can be called by remote peers using the `rpc` method. They support multiple partial responses in a single request.
        /// </remarks>
        /// <param name="scene">The scene to add the procedure to.</param>
        /// <param name="route">The route of the procedure</param>
        /// <param name="handler">A method that implement the procedure logic</param>
        /// <param name="ordered">True if order of the partial responses should be preserved when sent to the client, false otherwise.</param>
        public static void AddProcedure(this Scene scene, string route, Func<Stormancer.Plugins.RequestContext<IScenePeer>, Task> handler,bool ordered = true)
        {
            var rpcService = scene.GetComponent<Stormancer.Plugins.RpcClientPlugin.RpcService>();
            if (rpcService == null)
            {
                throw new NotSupportedException("RPC plugin not available.");
            }
            rpcService.AddProcedure(route, handler, ordered);
        }
    }
}
