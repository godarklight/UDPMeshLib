using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
namespace UDPMeshLib
{
    public class UdpMeshClient
    {
        public long CLIENT_TIMEOUT = TimeSpan.TicksPerMinute;
        private Thread runTask;
        private UdpClient clientSocketv4;
        private UdpClient clientSocketv6;
        private UdpPeer me;
        private IPAddress[] myAddresses;
        private IPEndPoint serverEndpointv4;
        private IPEndPoint serverEndpointv6;
        public bool connectedv4 = false;
        public bool connectedv6 = false;
        public long receivedStun;
        public long sendServerInfo;
        private bool error = false;
        private bool shutdown = false;
        private Dictionary<Guid, UdpPeer> clients = new Dictionary<Guid, UdpPeer>();
        private Dictionary<int, Action<byte[], int, Guid, IPEndPoint>> callbacks = new Dictionary<int, Action<byte[], int, Guid, IPEndPoint>>();
        private HashSet<string> contactedIPs = new HashSet<string>();
        private Action<string> debugLog;
        private byte[] v4buffer = new byte[2048];
        private byte[] v6buffer = new byte[2048];
        private byte[] relayBuffer = new byte[2048];
        private byte[] sendBuffer = new byte[2048];
        private byte[] buildBuffer = new byte[2048];
        private IPEndPoint anyV4 = new IPEndPoint(IPAddress.Any, 0);
        private IPEndPoint anyV6 = new IPEndPoint(IPAddress.IPv6Any, 0);

        /// <summary>
        /// Initializes a new instance of the <see cref="T:UDPMeshLib.UdpMeshClient"/> class.
        /// </summary>
        /// <param name="serverEndpointv4">Mesh servers IPv4 address to connect to.</param>
        /// <param name="serverEndpointv6">Mesh servers IPv6 address to connect to.</param>
        /// <param name="myAddresses">List of local addresses to inform the server about for in-network meshing</param>
        /// <param name="debugLog">Debug logging callback, leave <see langword="null"/> to disable</param>
        public UdpMeshClient(IPEndPoint serverEndpointv4, IPEndPoint serverEndpointv6, IPAddress[] myAddresses, Action<string> debugLog)
        {
            this.me = new UdpPeer(UdpMeshCommon.GetMeshAddress());
            this.myAddresses = myAddresses;
            this.serverEndpointv4 = serverEndpointv4;
            this.serverEndpointv6 = serverEndpointv6;
            this.debugLog = debugLog;
            callbacks[-1] = HandleServerReport;
            callbacks[-2] = HandleClientInfo;
            callbacks[-3] = HandleRelayMessage;
            callbacks[-201] = HandleHeartBeat;
            callbacks[-202] = HandleHeartBeatReply;
            UdpStun.callback = HandleStun;

        }

        void HandleStun(UdpStun.StunResult result)
        {
            if (result.success)
            {
                receivedStun = DateTime.UtcNow.Ticks;
                IPEndPoint endPoint = new IPEndPoint(result.remoteAddr, result.port);
                DebugLog("Adding stun result: " + endPoint);
                me.AddRemoteEndpoint(endPoint);
            }
        }


        private void DebugLog(string log)
        {
            if (debugLog != null)
            {
                debugLog(log);
            }
        }

        public void RegisterCallback(int type, Action<byte[], int, Guid, IPEndPoint> callback)
        {
            if (type < 0)
            {
                throw new IndexOutOfRangeException("Implementers must use positive type numbers");
            }
            callbacks[type] = callback;
        }

        private byte[] tempTime = new byte[8];
        private void HandleHeartBeatReply(byte[] inputData, int inputDataLength, Guid clientGuid, IPEndPoint iPEndPoint)
        {
            lock (tempTime)
            {
                if (inputDataLength != 40)
                {
                    return;
                }
                UdpPeer peer = GetPeer(clientGuid);
                if (peer == null)
                {
                    return;
                }
                lock (tempTime)
                {
                    Array.Copy(inputData, 24, tempTime, 0, 8);
                    UdpMeshCommon.FlipEndian(ref tempTime);
                    long sendTime = BitConverter.ToInt64(tempTime, 0);
                    Array.Copy(inputData, 32, tempTime, 0, 8);
                    UdpMeshCommon.FlipEndian(ref tempTime);
                    long remoteTime = BitConverter.ToInt64(tempTime, 0);
                    long receiveTime = DateTime.UtcNow.Ticks;
                    peer.lastReceiveTime = receiveTime;
                    if (UdpMeshCommon.IsIPv4(iPEndPoint.Address))
                    {
                        peer.latency4 = receiveTime - sendTime;
                        long expectedReceive = (peer.latency4 / 2) + sendTime;
                        peer.offset = expectedReceive - remoteTime;
                    }
                    if (UdpMeshCommon.IsIPv6(iPEndPoint.Address))
                    {
                        peer.latency6 = receiveTime - sendTime;
                        long expectedReceive = (peer.latency6 / 2) + sendTime;
                        peer.offset = expectedReceive - remoteTime;
                    }
                }
                peer.AddRemoteEndpoint(iPEndPoint);
                if (UdpMeshCommon.IsIPv4(iPEndPoint.Address) && !peer.usev4)
                {
                    peer.contactV4 = iPEndPoint;
                    peer.usev4 = true;
                }
                if (UdpMeshCommon.IsIPv6(iPEndPoint.Address) && !peer.usev6)
                {
                    peer.contactV6 = iPEndPoint;
                    peer.usev6 = true;
                }
            }
        }

        private byte[] heartbeatReplyBytes = new byte[16];
        private void HandleHeartBeat(byte[] inputData, int inputDataLength, Guid clientGuid, IPEndPoint iPEndPoint)
        {
            lock (buildBuffer)
            {
                lock (sendBuffer)
                {
                    if (inputDataLength != 32)
                    {
                        return;
                    }
                    UdpPeer peer = GetPeer(clientGuid);
                    if (peer == null)
                    {
                        return;
                    }
                    peer.lastReceiveTime = DateTime.UtcNow.Ticks;
                    byte[] currentTime = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
                    UdpMeshCommon.FlipEndian(ref currentTime);
                    Array.Copy(inputData, 24, heartbeatReplyBytes, 0, 8);
                    Array.Copy(currentTime, 0, heartbeatReplyBytes, 8, 8);
                    int length = UdpMeshCommon.GetPayload(-202, heartbeatReplyBytes, heartbeatReplyBytes.Length, sendBuffer);
                    if (UdpMeshCommon.IsIPv4(iPEndPoint.Address))
                    {
                        UdpMeshCommon.Send(clientSocketv4, sendBuffer, length, iPEndPoint);
                    }
                    if (UdpMeshCommon.IsIPv6(iPEndPoint.Address))
                    {
                        UdpMeshCommon.Send(clientSocketv6, sendBuffer, length, iPEndPoint);
                    }
                    if (UdpMeshCommon.IsIPv4(iPEndPoint.Address))
                    {
                        if (!peer.usev4)
                        {
                            Array.Copy(clientGuid.ToByteArray(), 0, buildBuffer, 0, 16);
                            buildBuffer[16] = 4;
                            byte[] endpointBytes = iPEndPoint.Address.GetAddressBytes();
                            if (endpointBytes.Length == 4)
                            {
                                Array.Copy(endpointBytes, 0, buildBuffer, 17, 4);
                            }
                            if (endpointBytes.Length == 16)
                            {
                                Array.Copy(endpointBytes, 12, buildBuffer, 17, 4);
                            }
                            byte[] portBytes = BitConverter.GetBytes((UInt16)iPEndPoint.Port);
                            UdpMeshCommon.FlipEndian(ref portBytes);
                            Array.Copy(portBytes, 0, buildBuffer, 21, 2);
                            int sendLength = UdpMeshCommon.GetPayload(-103, buildBuffer, 25, sendBuffer);
                            UdpMeshCommon.Send(clientSocketv4, sendBuffer, sendLength, serverEndpointv4);
                        }
                    }
                    if (UdpMeshCommon.IsIPv6(iPEndPoint.Address))
                    {
                        if (!peer.usev6)
                        {
                            Array.Copy(clientGuid.ToByteArray(), 0, buildBuffer, 0, 16);
                            buildBuffer[16] = 6;
                            byte[] endpointBytes = iPEndPoint.Address.GetAddressBytes();
                            Array.Copy(endpointBytes, 0, buildBuffer, 17, 16);
                            byte[] portBytes = BitConverter.GetBytes((UInt16)iPEndPoint.Port);
                            UdpMeshCommon.FlipEndian(ref portBytes);
                            Array.Copy(portBytes, 0, buildBuffer, 33, 2);
                            int sendLength = UdpMeshCommon.GetPayload(-103, buildBuffer, 35, sendBuffer);
                            UdpMeshCommon.Send(clientSocketv6, sendBuffer, sendLength, serverEndpointv6);
                        }
                    }
                }
            }
        }

        private void HandleRelayMessage(byte[] inputData, int inputDataLength, Guid serverGuid, IPEndPoint endPoint)
        {
            lock (relayBuffer)
            {
                if (inputDataLength < 64)
                {
                    return;
                }
                byte[] relayData = relayBuffer;
                if (!UdpMeshCommon.USE_BUFFERS)
                {
                    relayData = new byte[inputDataLength - 40];
                }
                Array.Copy(inputData, 0, relayData, 0, inputDataLength - 40);
                UdpMeshCommon.ProcessBytes(relayData, inputDataLength - 40, null, callbacks);
            }
        }

        private List<Guid> removeGuids = new List<Guid>();
        byte[] guidData = new byte[16];
        private void HandleServerReport(byte[] inputData, int inputDataLength, Guid serverGuid, IPEndPoint serverEndpoint)
        {
            lock (guidData)
            {
                if (UdpMeshCommon.IsIPv4(serverEndpoint.Address))
                {
                    connectedv4 = true;
                }
                if (UdpMeshCommon.IsIPv6(serverEndpoint.Address))
                {
                    connectedv6 = true;
                }
                int readPos = 24;
                lock (clients)
                {
                    removeGuids.AddRange(clients.Keys);
                    while (inputDataLength - readPos >= 16)
                    {
                        Array.Copy(inputData, readPos, guidData, 0, 16);
                        readPos += 16;
                        Guid newGuid = new Guid(guidData);
                        if (!clients.ContainsKey(newGuid))
                        {
                            clients.Add(newGuid, new UdpPeer(newGuid));
                        }
                        if (removeGuids.Contains(newGuid))
                        {
                            removeGuids.Remove(newGuid);
                        }
                    }
                    foreach (Guid guid in removeGuids)
                    {
                        clients.Remove(guid);
                    }
                    removeGuids.Clear();
                }
            }
        }

        private byte[] tempClientAddress4 = new byte[4];
        private byte[] tempClientAddress6 = new byte[16];
        private byte[] tempClientPort = new byte[2];
        private void HandleClientInfo(byte[] inputData, int inputDataLength, Guid serverGuid, IPEndPoint serverEndpoint)
        {
            lock (guidData)
            {
                int readPos = 24;
                if (inputDataLength - readPos < 17)
                {
                    return;
                }
                Array.Copy(inputData, readPos, guidData, 0, 16);
                Guid receiveID = new Guid(guidData);
                if (!clients.ContainsKey(receiveID))
                {
                    clients.Add(receiveID, new UdpPeer(receiveID));
                }
                UdpPeer client = clients[receiveID];
                readPos += 16;
                int v4Num = inputData[readPos];
                readPos++;
                for (int i = 0; i < v4Num; i++)
                {
                    Array.Copy(inputData, readPos, tempClientAddress4, 0, 4);
                    IPAddress ip = new IPAddress(tempClientAddress4);
                    readPos += 4;
                    Array.Copy(inputData, readPos, tempClientPort, 0, 2);
                    UdpMeshCommon.FlipEndian(ref tempClientPort);
                    int port = BitConverter.ToUInt16(tempClientPort, 0);
                    client.AddRemoteEndpoint(new IPEndPoint(ip, port));
                    readPos += 2;
                }
                if (inputDataLength - readPos < 1)
                {
                    return;
                }
                int v6Num = inputData[readPos];
                readPos++;
                for (int i = 0; i < v6Num; i++)
                {
                    Array.Copy(inputData, readPos, tempClientAddress6, 0, 16);
                    IPAddress ip = new IPAddress(tempClientAddress6);
                    readPos += 16;
                    Array.Copy(inputData, readPos, tempClientPort, 0, 2);
                    UdpMeshCommon.FlipEndian(ref tempClientPort);
                    int port = BitConverter.ToUInt16(tempClientPort, 0);
                    client.AddRemoteEndpoint(new IPEndPoint(ip, port));
                    readPos += 2;
                }
            }
        }

        /// <summary>
        /// Runs the server async
        /// </summary>
        public Thread Start()
        {
            runTask = new Thread(new ThreadStart(Run));
            runTask.Start();
            return runTask;
        }

        public void Run()
        {
            try
            {
                clientSocketv4 = new UdpClient(0, AddressFamily.InterNetwork);
            }
            catch (Exception e)
            {
                DebugLog("Error setting up v4 socket: " + e);
                clientSocketv4 = null;
            }
            try
            {
                clientSocketv6 = new UdpClient(0, AddressFamily.InterNetworkV6);
            }
            catch (Exception e)
            {
                DebugLog("Error setting up v6 socket: " + e);
                clientSocketv6 = null;
            }
            if (clientSocketv4 != null)
            {
                int v4portNumber = ((IPEndPoint)clientSocketv4.Client.LocalEndPoint).Port;
                DebugLog("Listening on port v4:" + v4portNumber);
                foreach (IPAddress addr in myAddresses)
                {
                    if (UdpMeshCommon.IsIPv4(addr))
                    {
                        me.AddRemoteEndpoint(new IPEndPoint(addr, v4portNumber));
                    }
                }
            }
            if (clientSocketv6 != null)
            {
                int v6portNumber = ((IPEndPoint)clientSocketv6.Client.LocalEndPoint).Port;
                DebugLog("Listening on port v6:" + v6portNumber);
                foreach (IPAddress addr in myAddresses)
                {
                    if (UdpMeshCommon.IsIPv6(addr))
                    {
                        me.AddRemoteEndpoint(new IPEndPoint(addr, v6portNumber));
                    }
                }
            }
            while (!shutdown && !error && clientSocketv4 != null && clientSocketv6 != null)
            {
                if ((DateTime.UtcNow.Ticks - sendServerInfo) > (TimeSpan.TicksPerSecond * 10))
                {
                    sendServerInfo = DateTime.UtcNow.Ticks;
                    if (clientSocketv4 != null || clientSocketv6 != null)
                    {
                        SendServerInfo();
                        SendClientsHello();
                    }
                    //Restun every 5 minutes
                    if ((DateTime.UtcNow.Ticks - receivedStun) > (TimeSpan.TicksPerMinute * 5))
                    {
                        if (clientSocketv4 != null)
                        {
                            UdpStun.RequestRemoteIPv4(clientSocketv4);
                            UdpStun.RequestRemoteIPv6(clientSocketv6);
                        }
                    }
                }
                if (clientSocketv4 != null)
                {
                    HandleReceive(clientSocketv4, false);
                }
                if (clientSocketv6 != null)
                {
                    HandleReceive(clientSocketv6, true);
                }
                Thread.Sleep(1);
            }
            Shutdown();
        }

        private void SendClientsHello()
        {
            lock (sendBuffer)
            {
                byte[] timeBytes = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
                UdpMeshCommon.FlipEndian(ref timeBytes);
                int sendBufferLength = UdpMeshCommon.GetPayload(-201, timeBytes, timeBytes.Length, sendBuffer);
                lock (clients)
                {
                    foreach (UdpPeer peer in clients.Values)
                    {
                        if (peer.guid == UdpMeshCommon.GetMeshAddress())
                        {
                            continue;
                        }
                        //Send to ipv4
                        if (peer.usev4)
                        {
                            UdpMeshCommon.Send(clientSocketv4, sendBuffer, sendBufferLength, peer.contactV4);
                        }
                        else
                        {
                            foreach (IPEndPoint iPEndPoint in peer.remoteEndpoints)
                            {
                                if (UdpMeshCommon.IsIPv4(iPEndPoint.Address))
                                {
                                    string newContactString = iPEndPoint.ToString();
                                    if (!contactedIPs.Contains(newContactString))
                                    {
                                        contactedIPs.Add(newContactString);
                                        DebugLog("Attempting new contact v4: " + newContactString);
                                    }
                                    UdpMeshCommon.Send(clientSocketv4, sendBuffer, sendBufferLength, iPEndPoint);
                                }
                            }
                        }
                        //Send to ipv6
                        if (peer.usev6)
                        {
                            UdpMeshCommon.Send(clientSocketv6, sendBuffer, sendBufferLength, peer.contactV6);
                        }
                        else
                        {
                            foreach (IPEndPoint iPEndPoint in peer.remoteEndpoints)
                            {
                                if (UdpMeshCommon.IsIPv6(iPEndPoint.Address))
                                {
                                    string newContactString = iPEndPoint.ToString();
                                    if (!contactedIPs.Contains(newContactString))
                                    {
                                        contactedIPs.Add(newContactString);
                                        DebugLog("Attempting new contact v6: " + newContactString);
                                    }
                                    UdpMeshCommon.Send(clientSocketv6, sendBuffer, sendBufferLength, iPEndPoint);
                                }
                            }
                        }
                    }
                }
            }
        }

        private void SendServerInfo()
        {
            byte[] serverData = me.GetServerEndpointMessage();
            int serverDataLength = me.GetEndpointMessageLength();
            if (serverEndpointv4 != null)
            {
                if (serverEndpointv4.Address != IPAddress.None)
                {
                    UdpMeshCommon.Send(clientSocketv4, serverData, serverDataLength, serverEndpointv4);
                }
            }
            if (serverEndpointv6 != null)
            {
                if (serverEndpointv6.Address != IPAddress.None)
                {
                    UdpMeshCommon.Send(clientSocketv6, serverData, serverDataLength, serverEndpointv6);
                }
            }
        }

        public void Shutdown()
        {
            if (shutdown)
            {
                return;
            }
            shutdown = true;
            if (clientSocketv4 != null)
            {
                clientSocketv4.Close();
            }
            if (clientSocketv6 != null)
            {
                clientSocketv6.Close();
            }
            clientSocketv4 = null;
            clientSocketv6 = null;
            runTask = null;
        }

        private void HandleReceive(UdpClient receiveClient, bool isv6)
        {
            try
            {
                if (receiveClient.Client.Available == 0)
                {
                    return;
                }
                byte[] buffer = v4buffer;
                EndPoint receiveAddrEndpoint = (EndPoint)anyV4;
                if (isv6)
                {
                    buffer = v6buffer;
                    receiveAddrEndpoint = (EndPoint)anyV6;
                }
                byte[] processBytes = buffer;
                int bytesRead = receiveClient.Client.ReceiveFrom(buffer, ref receiveAddrEndpoint);
                IPEndPoint receiveAddr = (IPEndPoint)receiveAddrEndpoint;
                bool process = true;
                if (bytesRead >= 24)
                {
                    if (process)
                    {
                        try
                        {
                            if (!UdpMeshCommon.USE_BUFFERS)
                            {
                                processBytes = new byte[bytesRead];
                                Array.Copy(buffer, 0, processBytes, 0, bytesRead);
                            }
                            UdpMeshCommon.ProcessBytes(processBytes, bytesRead, receiveAddr, callbacks);
                        }
                        catch (Exception e)
                        {
                            DebugLog("Error processing message: " + e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (!shutdown)
                {
                    Console.WriteLine("Error receiving: " + e);
                }

            }
        }

        public IEnumerable<UdpPeer> GetPeers()
        {
            List<UdpPeer> retVal = new List<UdpPeer>();
            lock (clients)
            {
                foreach (UdpPeer peer in clients.Values)
                {
                    retVal.Add(peer);
                }
            }
            return retVal;
        }

        public UdpPeer GetPeer(Guid client)
        {
            UdpPeer peer;
            clients.TryGetValue(client, out peer);
            return peer;
        }

        byte[] relayHeader;
        public void SendMessageToClient(Guid clientGuid, int type, byte[] data)
        {
            if (data != null)
            {
                SendMessageToClient(clientGuid, type, data, data.Length);
            }
            else
            {
                SendMessageToClient(clientGuid, type, null, 0);
            }
        }

        public void SendMessageToClient(Guid clientGuid, int type, byte[] data, int length)
        {
            lock (sendBuffer)
            {
                if (type < 0)
                {
                    throw new IndexOutOfRangeException("Implementers must use positive ID's");
                }
                UdpPeer peer = GetPeer(clientGuid);
                if (peer == null)
                {
                    return;
                }
                int sendBufferLength = UdpMeshCommon.GetPayload(type, data, length, sendBuffer);
                //We can use v4 or v6, let's select the lowest latency
                if (peer.usev4 && peer.usev6 && clientSocketv4 != null && clientSocketv6 != null)
                {
                    if (peer.latency4 < peer.latency6)
                    {
                        UdpMeshCommon.Send(clientSocketv4, sendBuffer, sendBufferLength, peer.contactV4);
                    }
                    else
                    {
                        UdpMeshCommon.Send(clientSocketv6, sendBuffer, sendBufferLength, peer.contactV6);
                    }
                    return;
                }
                //Have to use V6, only v6 contact
                if (peer.usev6 && !peer.usev4 && clientSocketv6 != null)
                {
                    UdpMeshCommon.Send(clientSocketv6, sendBuffer, sendBufferLength, peer.contactV6);
                    return;
                }
                //Have to use V6, only v4 contact
                if (peer.usev4 && !peer.usev6 && clientSocketv4 != null)
                {
                    UdpMeshCommon.Send(clientSocketv4, sendBuffer, sendBufferLength, peer.contactV4);
                    return;
                }
                //We currently have no contact and must send through the server to establish contact.
                int relayBufferLength = 40 + sendBufferLength;
                //Initialise header if this is the first run, we can use the relayBuffer as it is still free.
                if (relayHeader == null)
                {
                    int relayHeaderLength = UdpMeshCommon.GetPayload(-102, null, 0, relayBuffer);
                    relayHeader = new byte[relayHeaderLength];
                    Array.Copy(relayBuffer, 0, relayHeader, 0, relayHeaderLength);
                }
                Array.Copy(relayHeader, 0, relayBuffer, 0, relayHeader.Length);
                byte[] destination = clientGuid.ToByteArray();
                Array.Copy(destination, 0, relayBuffer, 24, 16);
                Array.Copy(sendBuffer, 0, relayBuffer, 40, sendBufferLength);
                if (connectedv6 && clientSocketv6 != null)
                {
                    UdpMeshCommon.Send(clientSocketv6, relayBuffer, relayBufferLength, serverEndpointv6);
                    return;
                }
                if (connectedv4 && clientSocketv4 != null)
                {
                    UdpMeshCommon.Send(clientSocketv4, relayBuffer, relayBufferLength, serverEndpointv4);
                    return;
                }
            }
        }
    }
}