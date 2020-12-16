// Ignorance 1.4.x
// Ignorance. It really kicks the Unity LLAPIs ass.
// https://github.com/SoftwareGuy/Ignorance
// -----------------
// Copyright (c) 2019 - 2020 Matt Coburn (SoftwareGuy/Coburn64)
// Ignorance Transport is licensed under the MIT license. Refer
// to the LICENSE file for more information.
// -----------------
// Ignorance Experimental (New) Version
// -----------------
using UnityEngine;
using ENet;
using System;
using System.Collections.Generic;
using System.Buffers;

namespace Mirror
{
    public class IgnoranceNew : Transport
    {
        #region Inspector options
        public int port = 7777;

        [Header("Logging Configuration")]
        [Tooltip("How verbose do you want Ignorance to be?")]
        public IgnoranceLogType LogType = IgnoranceLogType.Standard;

        [Header("Server Configuration")]
        [Tooltip("Should the server bind to all interfaces?")]
        public bool serverBindsAll = true;
        [Tooltip("This is only used if Server Binds All is unticked.")]
        public string serverBindAddress = string.Empty;
        [Tooltip("This tells ENet how many Peer slots to create. Helps performance, avoids looping over huge native arrays. Recommended: Max Mirror players, rounded to nearest 10. (Example: 16 -> 20).")]
        public int serverMaxPeerCapacity = 100;
        [Tooltip("How long ENet waits in native world. The higher this value, the more CPU usage. Lower values may/may not impact performance at high packet load.")]
        public int serverMaxNativeWaitTime = 1;

        [Header("Client Configuration")]
        [Tooltip("How long ENet waits in native world. The higher this value, the more CPU usage used. This is for the client, unlike the one above. Higher value probably trades CPU for more responsive networking.")]
        public int clientMaxNativeWaitTime = 3;
        [Tooltip("Interval between asking ENet for client status updates. Set to -1 to disable.")]
        public int clientStatusUpdateInterval = -1;

        [Header("Channel Configuration")]
        [Tooltip("You must define your channels in the array shown here, otherwise ENet will not know what channel delivery type to use.")]
        public IgnoranceChannelTypes[] Channels;

        [Header("Low-level Tweaking")]
        [Tooltip("For UDP based protocols, it's best to keep your data under the safe MTU of 1200 bytes. You can increase this, however beware this may open you up to allocation attacks.")]
        public int MaximumPacketSize = 33554432;
        #endregion

        #region Public Statistics
        public Stats Statistics;
        #endregion

#if MIRROR_26_0_OR_NEWER
        public override bool Available()
        {
            // Ignorance is not available for Unity WebGL, the PS4 (no dev kit to confirm) or Switch (port exists but I have no access to said code).
            // Ignorance is available for most other operating systems.
#if (UNITY_WEBGL || UNITY_PS4 || UNITY_SWITCH)
            return false;
#else
            return true;
#endif
        }

        public void Awake()
        {
            if (LogType != IgnoranceLogType.Nothing)
                print($"Thanks for using Ignorance {IgnoranceInternals.Version}. Keep up to date, report bugs and support the developer at https://github.com/SoftwareGuy/Ignorance!");
        }

        public override string ToString()
        {
            return "Ignorance 1.4.x Technicial Preview";
        }

        public override void ClientConnect(string address)
        {
            // Initialize.
            InitializeClientBackend();

            // Get going.
            ignoreDataPackets = false;
            NextStatusRequestUpdate = 0f;

            // Start!
            Client.Start();
        }

        public override void ClientConnect(Uri uri)
        {
            if (uri.Scheme != IgnoranceInternals.Scheme)
                throw new ArgumentException($"You used an invalid URI: {uri}. Please use {IgnoranceInternals.Scheme}://host:port instead", nameof(uri));

            if (!uri.IsDefaultPort)
            {
                // Set the communication port to the one specified.
                port = uri.Port;
            }

            ClientConnect(uri.Host);
        }

        public override bool ClientConnected() => isClientConnected;

        public override void ClientDisconnect()
        {
            if (Client != null)
            {
                Client.Outgoing.Enqueue(new IgnorancePacket { Type = IgnorancePacketType.ClientWantsToStop });
                Client.Stop();
            }

            ignoreDataPackets = true;
        }

        public override void ClientSend(int channelId, ArraySegment<byte> segment)
        {
            if (Client == null)
            {
                Debug.LogError("Client object is null, this shouldn't really happen but it did...");
                return;
            }

            if (channelId < 0 || channelId > Channels.Length)
            {
                Debug.LogError("Channel ID is out of bounds.");
                return;
            }

            // Create the dispatch packet.
            IgnorancePacket dispatchPacket = new IgnorancePacket
            {
                Outgoing = true,
                Channel = (byte)channelId,
                Length = segment.Count,
                Flags = (PacketFlags)Channels[channelId],
                // No need for native peer data here, client to server peer counter is always 0.
            };

            // Data portion of the packet.
            byte[] rental = ArrayPool<byte>.Shared.Rent(segment.Count > 1200 ? segment.Count : 1200);
            // Copy contents to the rented buffer, then assign it to the packet.
            segment.Array.CopyTo(rental, 0);
            dispatchPacket.RentedByteArray = rental;
            // Enqueue.
            Client.Outgoing.Enqueue(dispatchPacket);
        }

        public override int GetMaxPacketSize(int channelId = 0) => MaximumPacketSize;

        public override bool ServerActive()
        {
            return Server != null ? Server.IsAlive : false;
        }

        public override bool ServerDisconnect(int connectionId)
        {
            // Debug.LogWarning("TODO: Server Disconnect");

            if (Server == null)
            {
                Debug.LogError("Server object is null, this shouldn't really happen but it did...");
                return false;
            }

            if (ConnectionLookupDict.ContainsKey(connectionId))
            {
                IgnoranceCommandPacket kickPacket = new IgnoranceCommandPacket
                {
                    Type = IgnoranceCommandType.ServerKickPeer,
                    PeerId = ConnectionLookupDict[connectionId].NativePeerId
                };

                Server.Commands.Enqueue(kickPacket);
            }
            else
            {
                if (LogType != IgnoranceLogType.Nothing)
                    Debug.LogWarning($"Who is connection {connectionId}? I don't know them.");
                return false;
            }

            return true;
        }

        public override string ServerGetClientAddress(int connectionId)
        {
            if (ConnectionLookupDict.TryGetValue(connectionId, out PeerConnectionData details))
            {
                return $"{details.IP}:{details.Port}";
            }

            return "(unavailable)";
        }

        public override void ServerSend(int connectionId, int channelId, ArraySegment<byte> segment)
        {
            // Debug.Log($"ServerSend({connectionId}, {channelId}, <{segment.Count} byte segment>)");

            if (Server == null)
            {
                Debug.LogError("Server object is null, this shouldn't really happen but it did...");
                return;
            }

            if (channelId < 0 || channelId > Channels.Length)
            {
                Debug.LogError("Channel ID is out of bounds.");
                return;
            }

            // Create the dispatch packet.
            IgnoranceOutgoingPacket dispatchPacket = new IgnoranceOutgoingPacket
            {
                Channel = (byte)channelId,

                Length = segment.Count,
                Flags = (PacketFlags)Channels[channelId],
                NativePeerId = ConnectionLookupDict[connectionId].NativePeerId
            };

            // Data portion of the packet.
            if(segment.Count <= 1200)
            {
                dispatchPacket.RentedArray = ArrayPool<byte>.Shared.Rent(1200);
                dispatchPacket.WasRented = true;
            } else if (segment.Count <= 102400)
            {
                dispatchPacket.RentedArray = ArrayPool<byte>.Shared.Rent(102400);
                dispatchPacket.WasRented = true;
            } else
            {
                dispatchPacket.RentedArray = new byte[segment.Count];
                dispatchPacket.WasRented = false;
            }

            segment.Array.CopyTo(dispatchPacket.RentedArray, 0);

            // Add it to the outgoing queue.
            Server.Outgoing.Enqueue(dispatchPacket);
        }

        public override void ServerStart()
        {
            if (LogType != IgnoranceLogType.Nothing)
                print("Ignorance Server Instance is starting up...");

            InitializeServerBackend();

            Server.Start();
            isServerActive = true;
        }

        public override void ServerStop()
        {
            if (Server != null)
            {
                if (LogType != IgnoranceLogType.Nothing)
                    print("Ignorance Server Instance is shutting down...");

                Server.Stop();
            }

            ENetPeerToMirrorLookup.Clear();
            ConnectionLookupDict.Clear();

            isServerActive = false;
        }

        public override Uri ServerUri()
        {
            UriBuilder builder = new UriBuilder
            {
                Scheme = IgnoranceInternals.Scheme,
                Host = serverBindAddress,
                Port = port
            };

            return builder.Uri;
        }

        public override void Shutdown()
        {
            Debug.LogWarning("TODO: Shutdown");
        }

        // Check to ensure channels 0 and 1 mimic LLAPI. Override this at your own risk.
        private void OnValidate()
        {
            if (Channels != null && Channels.Length >= 2)
            {
                // Check to make sure that Channel 0 and 1 are correct.
                if (Channels[0] != IgnoranceChannelTypes.Reliable) Channels[0] = IgnoranceChannelTypes.Reliable;
                if (Channels[1] != IgnoranceChannelTypes.Unreliable) Channels[1] = IgnoranceChannelTypes.Unreliable;
            }
            else
            {
                Channels = new IgnoranceChannelTypes[2]
                {
                    IgnoranceChannelTypes.Reliable,
                    IgnoranceChannelTypes.Unreliable
                };
            }

            // ENet only supports a maximum of 32MB packet size.
            if (MaximumPacketSize > 33554432) MaximumPacketSize = 33554432;
        }

        #region Inner workings
        private bool isServerActive, isClientConnected, ignoreDataPackets;
        private string cachedConnectionAddress = string.Empty;
        private IgnoranceServer Server = new IgnoranceServer();
        private IgnoranceClient Client = new IgnoranceClient();

        private Dictionary<int, PeerConnectionData> ConnectionLookupDict = new Dictionary<int, PeerConnectionData>();
        private Dictionary<uint, int> ENetPeerToMirrorLookup = new Dictionary<uint, int>();
        private int ConnId = 1;
        private float NextStatusRequestUpdate = 0f;

        private void InitializeServerBackend()
        {
            if (Server == null)
            {
                Debug.LogWarning("IgnoranceServer reference for Server mode was null. This shouldn't happen, but to be safe we'll reinitialize it.");
                Server = new IgnoranceServer();
            }

            // Set up the new IgnoranceServer reference.
            if (serverBindsAll)
            {
                // MacOS is special. It's also a massive thorn in my backside.
                Server.BindAddress = IgnoranceInternals.BindAllFuckingAppleMacs;
            }
            else
            {
                // Use the supplied bind address.
                Server.BindAddress = serverBindAddress;
            }

            Server.BindPort = port;
            Server.MaximumPeers = serverMaxPeerCapacity;
            Server.MaximumChannels = Channels.Length;
            Server.PollTime = serverMaxNativeWaitTime;
            Server.MaximumPacketSize = MaximumPacketSize;
        }

        private void InitializeClientBackend()
        {
            if (Client == null)
            {
                Debug.LogWarning("Ignorance: IgnoranceClient reference for Client mode was null. This shouldn't happen, but to be safe we'll reinitialize it.");
                Client = new IgnoranceClient();
            }

            Client.ConnectAddress = cachedConnectionAddress;
            Client.ConnectPort = port;
            Client.ExpectedChannels = Channels.Length;
            Client.PollTime = clientMaxNativeWaitTime;
            Client.MaximumPacketSize = MaximumPacketSize;
            Client.Verbosity = (int)LogType;
        }

        private void ProcessServerPackets()
        {
            // Step 1: Handle incoming data packets.

            while (Server.Incoming.TryDequeue(out IgnoranceIncomingPacket incomingPacket))
            {
                // print($"Server got one. It's a {incomingPacket.Type}");
                if (ENetPeerToMirrorLookup.ContainsKey(incomingPacket.NativePeerId))
                {
                    // print("YEAH!");
                    // print($"Byte array: {incomingPacket.RentedByteArray.Length}. Packet Length: {incomingPacket.Length}");

                    // We know who's it is from, let's process it.
                    int conn = ENetPeerToMirrorLookup[incomingPacket.NativePeerId];
                    ArraySegment<byte> dataSegment = new ArraySegment<byte>(incomingPacket.RentedArray, 0, incomingPacket.Length);

                    OnServerDataReceived?.Invoke(conn, dataSegment, incomingPacket.Channel);
                }

                // Release the array back to the pool if it was rented.
                if(incomingPacket.WasRented)
                    ArrayPool<byte>.Shared.Return(incomingPacket.RentedArray, true);

                // Some messages can disable the transport
                // If the transport was disabled by any of the messages, we have to break out of the loop and wait until we've been re-enabled.
                if (!enabled)
                {
                    break;
                }
            }

            // Step 2: Handle incoming connection events.
            while (Server.ConnectionEvents.TryDequeue(out IgnoranceConnectionEvent connectionEvent))
            {
                // Was this a Disconnection?
                if (connectionEvent.WasDisconnect)
                {
                    // If it doesn't exist in our dictionary, then it's likely a ghost or malicious.
                    if (!ENetPeerToMirrorLookup.ContainsKey(connectionEvent.NativePeerId)) continue;

                    int key = ENetPeerToMirrorLookup[connectionEvent.NativePeerId];

                    ConnectionLookupDict.Remove(key);
                    ENetPeerToMirrorLookup.Remove(connectionEvent.NativePeerId);

                    // Invoke Mirror handler.
                    OnServerDisconnected?.Invoke(key);
                }
                else
                {
                    // Nah mate, just a regular connection.
                    ConnectionLookupDict.Add(ConnId, new PeerConnectionData
                    {
                        NativePeerId = connectionEvent.NativePeerId,
                        IP = connectionEvent.IP,
                        Port = connectionEvent.Port
                    });

                    if (ENetPeerToMirrorLookup.ContainsKey(connectionEvent.NativePeerId))
                    {
                        Debug.LogWarning($"This is weird - we already know Native Peer {connectionEvent.NativePeerId} as Conn {ConnId}. Replacing, but this may cause issues.");
                        ENetPeerToMirrorLookup[connectionEvent.NativePeerId] = ConnId;
                    }
                    else
                    {
                        ENetPeerToMirrorLookup.Add(connectionEvent.NativePeerId, ConnId);
                    }

                    OnServerConnected?.Invoke(ConnId);
                    ConnId++;
                }
            }
        }

        private void ProcessClientPackets()
        {
            while (Client.Incoming.TryDequeue(out IgnorancePacket incomingPacket))
            {
                // print($"Client got one. It's a {incomingPacket.Type}");
                switch (incomingPacket.Type)
                {
                    case IgnorancePacketType.ClientConnect:
                        // Client connected to the server, advise Mirror.                        
                        isClientConnected = true;
                        OnClientConnected?.Invoke();
                        break;

                    case IgnorancePacketType.ClientDisconnect:
                        // Client disconnected from server, advise Mirror.
                        OnClientDisconnected?.Invoke();
                        isClientConnected = false;
                        break;

                    case IgnorancePacketType.ClientData:
                        // Temporary fix: if ENet thread is too fast for Mirror, then ignore the packet.
                        // This is seen sometimes if you stop the client and there's still stuff in the queue.
                        if (!isClientConnected)
                        {
                            ArrayPool<byte>.Shared.Return(incomingPacket.RentedByteArray, true);
                            break;
                        }

                        // Otherwise client recieved data, advise Mirror.
                        // print($"Byte array: {incomingPacket.RentedByteArray.Length}. Packet Length: {incomingPacket.Length}");
                        ArraySegment<byte> dataSegment = new ArraySegment<byte>(incomingPacket.RentedByteArray, 0, incomingPacket.Length);
                        OnClientDataReceived?.Invoke(dataSegment, incomingPacket.Channel);
                        // Cleanup.
                        ArrayPool<byte>.Shared.Return(incomingPacket.RentedByteArray, true);
                        break;

                    case IgnorancePacketType.ClientStatusUpdateResponse:
                        // Updates all statistics.
                        Statistics.RTT = incomingPacket.StatusData.RTT;
                        Statistics.ReceivedBytes = incomingPacket.StatusData.BytesReceived;
                        Statistics.SentBytes = incomingPacket.StatusData.BytesSent;
                        Statistics.SentPackets = incomingPacket.StatusData.PacketsSent;
                        break;
                }

                // Some messages can disable the transport
                // If the transport was disabled by any of the messages, we have to break out of the loop and wait until we've been re-enabled.
                if (!enabled)
                {
                    break;
                }
            }
        }

        private void LateUpdate()
        {
            if (enabled)
            {
                if (Server.IsAlive)
                    ProcessServerPackets();

                if (Client.IsAlive)
                {
                    ProcessClientPackets();

                    if (isClientConnected && clientStatusUpdateInterval > -1 && Time.time >= NextStatusRequestUpdate)
                    {
                        Client.Outgoing.Enqueue(new IgnorancePacket { Outgoing = true, Type = IgnorancePacketType.ClientStatusUpdateRequest });
                        NextStatusRequestUpdate = Time.time + clientStatusUpdateInterval;
                    }
                }
            }
        }
        #endregion

        public struct Stats
        {
            public uint RTT;
            public ulong SentBytes;
            public ulong SentPackets;
            public ulong ReceivedBytes;
        }
#endif
    }
}