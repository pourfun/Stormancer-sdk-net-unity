﻿using Stormancer.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Stormancer.Plugins
{
    /// <summary>
    /// Context used to interact with a request on the server
    /// </summary>
    /// <typeparam name="T">Type of the remote peer</typeparam>
    public class RequestContext<T> where T : IScenePeerClient
    {
        private ISceneHost _scene;
        private ushort id;
        private bool _ordered;
        private T _peer;
        private byte _msgSent;
        /// <summary>
        /// Remote peer
        /// </summary>
        public T RemotePeer
        {
            get
            {
                return _peer;
            }
        }

        /// <summary>
        /// Stream instance containing the binary data sent with the request.
        /// </summary>
        public Stream InputStream
        {
            get;
            private set;
        }

        internal RequestContext(T peer, ISceneHost scene, ushort id, bool ordered, Stream inputStream)
        {
           
            this._scene = scene;
            this.id = id;
            this._ordered = ordered;
            this._peer = peer;
            this.InputStream = inputStream;
        }

        private void WriteRequestId(Stream s)
        {
            s.Write(BitConverter.GetBytes(id), 0, 2);
        }

        /// <summary>
        /// Sends a partial response to the client through the request channel
        /// </summary>
        /// <param name="writer">An action that writes the response message</param>
        /// <param name="priority">A priority level to use to send the message</param>
        /// <remarks>
        /// All request responses are sent using the RELIABLE_ORDERED reliability level
        /// On the client, code that subscribed to the observable returned by the request send method will receive the messages 
        /// sent through the `SendValue` method.
        /// </remarks>
        public void SendValue(Action<Stream> writer, PacketPriority priority)
        {
            _scene.Send(new MatchPeerFilter(_peer),RpcHostPlugin.NextRouteName, s =>
            {
                
                WriteRequestId(s);
                writer(s);
            }, priority, this._ordered ? PacketReliability.RELIABLE_ORDERED : PacketReliability.RELIABLE);
            _msgSent = 1;
        }


        internal void SendError(string errorMsg)
        {
            this._scene.Send(new MatchPeerFilter(_peer), RpcHostPlugin.ErrorRouteName, s =>
            {
                WriteRequestId(s);
                _peer.Serializer().Serialize(errorMsg, s);
            }, PacketPriority.MEDIUM_PRIORITY, PacketReliability.RELIABLE_ORDERED);
        }

        internal void SendCompleted()
        {
            this._scene.Send(new MatchPeerFilter(_peer),RpcHostPlugin.CompletedRouteName, s =>
            {
                s.WriteByte(_msgSent);
                WriteRequestId(s);
            }, PacketPriority.MEDIUM_PRIORITY, PacketReliability.RELIABLE_ORDERED);
        }
    }
}