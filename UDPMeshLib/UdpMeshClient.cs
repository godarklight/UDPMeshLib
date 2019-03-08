using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
namespace UDPMeshLib
{
    public class UdpMeshClient
    {
        public long CLIENT_TIMEOUT = TimeSpan.TicksPerMinute;
        private Task runTask;
        private UdpClient clientSocketv4;
        private UdpClient clientSocketv6;
        private UdpPeer me;
        private IPAddress[] myAddresses;
        private IPEndPoint serverEndpointv4;
        private IPEndPoint serverEndpointv6;
        public bool connectedv4 = false;
        public bool connectedv6 = false;
        public long receivedStun;
        private bool error = false;
        private Dictionary<Guid, UdpPeer> clients = new Dictionary<Guid, UdpPeer>();
        private Dictionary<int, Action<byte[], Guid, IPEndPoint>> callbacks = new Dictionary<int, Action<byte[], Guid, IPEndPoint>>();
        private HashSet<string> contactedIPs = new HashSet<string>();

        public UdpMeshClient(IPEndPoint serverEndpointv4, IPEndPoint serverEndpointv6, IPAddress[] myAddresses)
        {
            this.me = new UdpPeer(UdpMeshCommon.GetMeshAddress());
            this.myAddresses = myAddresses;
            this.serverEndpointv4 = serverEndpointv4;
            this.serverEndpointv6 = serverEndpointv6;
            callbacks[-1] = HandleServerReport;
            callbacks[-2] = HandleClientInfo;
            callbacks[-3] = HandleRelayMessage;
            callbacks[-201] = HandleHeartBeat;
            callbacks[-202] = HandleHeartBeatReply;
            callbacks[int.MinValue] = HandleStun;
        }

        private void HandleStun(byte[] inputData, Guid clientGuid, IPEndPoint iPEndPoint)
        {
            if (inputData.Length < 20)
            {
                return;
            }
            byte[] messageShortBytes = new byte[2];
            Array.Copy(inputData, 2, messageShortBytes, 0, 2);
            UdpMeshCommon.FlipEndian(ref messageShortBytes);
            int messageLength = BitConverter.ToInt16(messageShortBytes, 0);
            byte[] messageGuidBytes = new byte[16];
            Array.Copy(inputData, 4, messageGuidBytes, 0, 16);
            Guid receiveGuid = new Guid(messageGuidBytes);
            int bytesToRead = messageLength;
            while (bytesToRead > 0)
            {
                if (bytesToRead < 4)
                {
                    return;
                }
                Array.Copy(inputData, inputData.Length - bytesToRead, messageShortBytes, 0, 2);
                UdpMeshCommon.FlipEndian(ref messageShortBytes);
                int attrType = BitConverter.ToUInt16(messageShortBytes, 0);
                bytesToRead -= 2;
                Array.Copy(inputData, inputData.Length - bytesToRead, messageShortBytes, 0, 2);
                UdpMeshCommon.FlipEndian(ref messageShortBytes);
                int attrLength = BitConverter.ToUInt16(messageShortBytes, 0);
                bytesToRead -= 2;
                if (attrLength > 0)
                {
                    byte[] attrBytes = new byte[attrLength];
                    Array.Copy(inputData, inputData.Length - bytesToRead, attrBytes, 0, attrLength);
                    bytesToRead -= attrLength;
                    if (attrType == 1 && attrBytes[1] == 1 && attrLength == 8)
                    {
                        Array.Copy(attrBytes, 2, messageShortBytes, 0, 2);
                        UdpMeshCommon.FlipEndian(ref messageShortBytes);
                        int srcPort = BitConverter.ToUInt16(messageShortBytes, 0);
                        byte[] ipBytes = new byte[4];
                        Array.Copy(attrBytes, 4, ipBytes, 0, 4);
                        IPAddress srcAddr = new IPAddress(ipBytes);
                        receivedStun = DateTime.UtcNow.Ticks;
                        IPEndPoint stunEndpoint = new IPEndPoint(srcAddr, srcPort);
                        Console.WriteLine("Adding stun address: " + stunEndpoint);
                        me.AddRemoteEndpoint(stunEndpoint);
                    }
                }
            }
        }

        public void RegisterCallback(int type, Action<byte[], Guid, IPEndPoint> callback)
        {
            if (type > 0)
            {
                throw new IndexOutOfRangeException("Implementers must use positive type numbers");
            }
            callbacks[type] = callback;
        }

        byte[] tempTime = new byte[8];
        private void HandleHeartBeatReply(byte[] inputData, Guid clientGuid, IPEndPoint iPEndPoint)
        {
            if (inputData.Length != 40)
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

        private void HandleHeartBeat(byte[] inputData, Guid clientGuid, IPEndPoint iPEndPoint)
        {
            if (inputData.Length != 32)
            {
                return;
            }
            UdpPeer peer = GetPeer(clientGuid);
            if (peer == null)
            {
                return;
            }
            peer.lastReceiveTime = DateTime.UtcNow.Ticks;
            byte[] replyBytes = new byte[16];
            byte[] currentTime = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
            UdpMeshCommon.FlipEndian(ref currentTime);
            Array.Copy(inputData, 24, replyBytes, 0, 8);
            Array.Copy(currentTime, 0, replyBytes, 8, 8);
            byte[] replyMessage = UdpMeshCommon.GetPayload(-202, replyBytes);
            if (UdpMeshCommon.IsIPv4(iPEndPoint.Address))
            {
                UdpMeshCommon.Send(clientSocketv4, replyMessage, iPEndPoint);
                if (!peer.usev4)
                {
                    byte[] notifyServerOfEndpoint = new byte[23];
                    Array.Copy(clientGuid.ToByteArray(), 0, notifyServerOfEndpoint, 0, 16);
                    notifyServerOfEndpoint[16] = 4;
                    byte[] endpointBytes = iPEndPoint.Address.GetAddressBytes();
                    if (endpointBytes.Length == 4)
                    {
                        Array.Copy(endpointBytes, 0, notifyServerOfEndpoint, 17, 4);
                    }
                    if (endpointBytes.Length == 16)
                    {
                        Array.Copy(endpointBytes, 12, notifyServerOfEndpoint, 17, 4);
                    }
                    byte[] portBytes = BitConverter.GetBytes((UInt16)iPEndPoint.Port);
                    UdpMeshCommon.FlipEndian(ref portBytes);
                    Array.Copy(portBytes, 0, notifyServerOfEndpoint, 21, 2);
                    byte[] notifyServerOfEndpointPayload = UdpMeshCommon.GetPayload(-103, notifyServerOfEndpoint);
                    UdpMeshCommon.Send(clientSocketv4, notifyServerOfEndpointPayload, serverEndpointv4);
                }
            }
            if (UdpMeshCommon.IsIPv6(iPEndPoint.Address))
            {
                UdpMeshCommon.Send(clientSocketv6, replyMessage, iPEndPoint);
                if (!peer.usev6)
                {
                    byte[] notifyServerOfEndpoint = new byte[35];
                    Array.Copy(clientGuid.ToByteArray(), 0, notifyServerOfEndpoint, 0, 16);
                    notifyServerOfEndpoint[16] = 6;
                    byte[] endpointBytes = iPEndPoint.Address.GetAddressBytes();
                    Array.Copy(endpointBytes, 0, notifyServerOfEndpoint, 17, 16);
                    byte[] portBytes = BitConverter.GetBytes((UInt16)iPEndPoint.Port);
                    UdpMeshCommon.FlipEndian(ref portBytes);
                    Array.Copy(portBytes, 0, notifyServerOfEndpoint, 33, 2);
                    byte[] notifyServerOfEndpointPayload = UdpMeshCommon.GetPayload(-103, notifyServerOfEndpoint);
                    UdpMeshCommon.Send(clientSocketv6, notifyServerOfEndpointPayload, serverEndpointv6);
                }
            }
        }

        private void HandleRelayMessage(byte[] inputData, Guid serverGuid, IPEndPoint endPoint)
        {
            if (inputData.Length < 64)
            {
                return;
            }
            byte[] cropMessage = new byte[inputData.Length - 40];
            Array.Copy(inputData, 40, cropMessage, 0, cropMessage.Length);
            UdpMeshCommon.ProcessBytes(cropMessage, null, callbacks);
        }

        private List<Guid> removeGuids = new List<Guid>();
        private void HandleServerReport(byte[] inputData, Guid serverGuid, IPEndPoint serverEndpoint)
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
            byte[] guidData = new byte[16];
            lock (clients)
            {
                removeGuids.AddRange(clients.Keys);
                while (inputData.Length - readPos >= 16)
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

        private byte[] tempClientAddress4 = new byte[4];
        private byte[] tempClientAddress6 = new byte[16];
        private byte[] tempClientPort = new byte[2];
        private void HandleClientInfo(byte[] inputData, Guid serverGuid, IPEndPoint serverEndpoint)
        {
            int readPos = 24;
            if (inputData.Length - readPos < 17)
            {
                return;
            }
            byte[] guidData = new byte[16];
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
                if (inputData.Length - readPos < 6)
                {
                    return;
                }
                Array.Copy(inputData, readPos, tempClientAddress4, 0, 4);
                IPAddress ip = new IPAddress(tempClientAddress4);
                readPos += 4;
                Array.Copy(inputData, readPos, tempClientPort, 0, 2);
                UdpMeshCommon.FlipEndian(ref tempClientPort);
                int port = BitConverter.ToUInt16(tempClientPort, 0);
                client.AddRemoteEndpoint(new IPEndPoint(ip, port));
                readPos += 2;
            }
            if (inputData.Length - readPos < 1)
            {
                return;
            }
            int v6Num = inputData[readPos];
            readPos++;
            for (int i = 0; i < v6Num; i++)
            {
                if (inputData.Length - readPos < 18)
                {
                    return;
                }
                Array.Copy(inputData, readPos, tempClientAddress6, 0, 16);
                IPAddress ip = new IPAddress(tempClientAddress6);
                readPos += 16;
                Array.Copy(inputData, readPos, tempClientPort, 0, 2);
                UdpMeshCommon.FlipEndian(ref tempClientPort);
                int port = BitConverter.ToUInt16(tempClientPort, 0);
                client.AddRemoteEndpoint(new IPEndPoint(ip, port));
                readPos += 2;
            }
            /*
            Console.WriteLine("Updated endpoints for " + receiveID);
            foreach (IPEndPoint endPoint in client.remoteEndpoints)
            {
                Console.WriteLine(endPoint);
            }
            Console.WriteLine("===");
            */
        }

        /// <summary>
        /// Runs the server async
        /// </summary>
        public async Task Start()
        {
            runTask = Task.Run(() => Run());
            await runTask;
        }

        public void Run()
        {
            try
            {
                clientSocketv4 = new UdpClient(0, AddressFamily.InterNetwork);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error setting up v4 socket: " + e);
                clientSocketv4 = null;
            }
            try
            {
                clientSocketv6 = new UdpClient(0, AddressFamily.InterNetworkV6);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error setting up v6 socket: " + e);
                clientSocketv6 = null;
            }
            if (clientSocketv4 != null)
            {
                clientSocketv4.BeginReceive(HandleReceive, clientSocketv4);
            }
            if (clientSocketv6 != null)
            {
                clientSocketv6.BeginReceive(HandleReceive, clientSocketv6);
            }
            if (clientSocketv4 != null)
            {
                int v4portNumber = ((IPEndPoint)clientSocketv4.Client.LocalEndPoint).Port;
                Console.WriteLine("Listening on port v4:" + v4portNumber);
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
                Console.WriteLine("Listening on port v6:" + v6portNumber);
                foreach (IPAddress addr in myAddresses)
                {
                    if (UdpMeshCommon.IsIPv6(addr))
                    {
                        me.AddRemoteEndpoint(new IPEndPoint(addr, v6portNumber));
                    }
                }
            }
            while (clientSocketv4 != null && clientSocketv6 != null)
            {
                if (error)
                {
                    break;
                }
                //Restun every 5 minutes
                if ((DateTime.UtcNow.Ticks - receivedStun) > (TimeSpan.TicksPerMinute * 5))
                {
                    if (clientSocketv4 != null)
                    {
                        UdpStun.RequestRemoteIP(clientSocketv4);
                    }
                }
                SendServerInfo();
                SendClientsHello();
                System.Threading.Thread.Sleep(10000);
            }
            Shutdown();
        }

        private void SendClientsHello()
        {
            byte[] timeBytes = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
            UdpMeshCommon.FlipEndian(ref timeBytes);
            byte[] sendBytes = UdpMeshCommon.GetPayload(-201, timeBytes);
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
                        UdpMeshCommon.Send(clientSocketv4, sendBytes, peer.contactV4);
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
                                    Console.WriteLine("Attempting new contact v4: " + newContactString);
                                }
                                UdpMeshCommon.Send(clientSocketv4, sendBytes, iPEndPoint);
                            }
                        }
                    }
                    //Send to ipv6
                    if (peer.usev6)
                    {
                        UdpMeshCommon.Send(clientSocketv6, sendBytes, peer.contactV6);
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
                                    Console.WriteLine("Attempting new contact v6: " + newContactString);
                                }
                                UdpMeshCommon.Send(clientSocketv6, sendBytes, iPEndPoint);
                            }
                        }
                    }

                }
            }
        }

        private void SendServerInfo()
        {
            byte[] serverData = me.GetServerEndpointMessage();
            if (serverEndpointv4.Address != IPAddress.None)
            {
                UdpMeshCommon.Send(clientSocketv4, serverData, serverEndpointv4);
            }
            if (serverEndpointv6.Address != IPAddress.None)
            {
                UdpMeshCommon.Send(clientSocketv6, serverData, serverEndpointv6);
            }
        }

        public void Shutdown()
        {
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

        private void HandleReceive(IAsyncResult ar)
        {
            UdpClient receiveClient = (UdpClient)ar.AsyncState;
            try
            {
                IPEndPoint receiveAddr = null;
                byte[] receiveBytes = receiveClient.EndReceive(ar, ref receiveAddr);
                byte[] magicBytes = UdpMeshCommon.GetMagicHeader();
                bool process = true;
                if (receiveBytes.Length >= 24)
                {
                    if (process)
                    {
                        try
                        {
                            UdpMeshCommon.ProcessBytes(receiveBytes, receiveAddr, callbacks);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error processing message: " + e);
                        }
                    }
                }

            }
            catch (Exception e)
            {
                Console.WriteLine("Error receiving: " + e);
            }
            try
            {
                receiveClient.BeginReceive(HandleReceive, receiveClient);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error restarting receiving: " + e);
                error = true;
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

        byte[] relayHeader = UdpMeshCommon.GetPayload(-102, null);
        public void SendMessageToClient(Guid clientGuid, int type, byte[] data)
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
            byte[] sendBytes = UdpMeshCommon.GetPayload(type, data);
            if (peer.usev4 && peer.usev6 && clientSocketv4 != null && clientSocketv6 != null)
            {
                if (peer.latency4 < peer.latency6)
                {
                    UdpMeshCommon.Send(clientSocketv4, sendBytes, peer.contactV4);
                }
                else
                {
                    UdpMeshCommon.Send(clientSocketv6, sendBytes, peer.contactV6);
                }
                return;
            }
            if (peer.usev6 && clientSocketv6 != null)
            {
                UdpMeshCommon.Send(clientSocketv6, sendBytes, peer.contactV6);
                return;
            }
            if (peer.usev4 && clientSocketv4 != null)
            {
                UdpMeshCommon.Send(clientSocketv4, sendBytes, peer.contactV4);
                return;
            }
            byte[] relayBytes = new byte[40 + sendBytes.Length];
            Array.Copy(relayHeader, 0, relayBytes, 0, relayHeader.Length);
            byte[] destination = clientGuid.ToByteArray();
            Array.Copy(destination, 0, relayBytes, 24, 16);
            Array.Copy(sendBytes, 0, relayBytes, 40, sendBytes.Length);
            if (connectedv6 && clientSocketv6 != null)
            {
                UdpMeshCommon.Send(clientSocketv6, relayBytes, serverEndpointv6);
                return;
            }
            if (connectedv4 && clientSocketv4 != null)
            {
                UdpMeshCommon.Send(clientSocketv4, relayBytes, serverEndpointv4);
                return;
            }
        }
    }
}