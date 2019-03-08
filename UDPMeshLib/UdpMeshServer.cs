using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace UDPMeshLib
{
    public class UdpMeshServer
    {
        public long CLIENT_TIMEOUT = TimeSpan.TicksPerMinute;
        private int portNumber;
        private Task runTask;
        private UdpClient serverSocketv4;
        private UdpClient serverSocketv6;
        private bool error = false;
        private Dictionary<Guid, UdpPeer> clients = new Dictionary<Guid, UdpPeer>();
        private Dictionary<int, Action<byte[], Guid, IPEndPoint>> callbacks = new Dictionary<int, Action<byte[], Guid, IPEndPoint>>();
        public UdpMeshServer(int portNumber)
        {
            this.portNumber = portNumber;
            callbacks[-101] = ClientReport;
            callbacks[-102] = RelayMessage;
            callbacks[-103] = ClientExternalReport;
        }


        public void RegisterCallback(int type, Action<byte[], Guid, IPEndPoint> callback)
        {
            if (type > 0)
            {
                throw new IndexOutOfRangeException("Implementers must use positive type numbers");
            }
            callbacks[type] = callback;
        }

        /// <summary>
        /// Runs the server async
        /// </summary>
        public async Task Start()
        {
            runTask = Task.Run(() => Run());
            await runTask;
        }

        /// <summary>
        /// Runs the server and blocks
        /// </summary>
        public void Run()
        {
            try
            {
                serverSocketv6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Any, portNumber));
            }
            catch
            {
                serverSocketv6 = null;
            }
            try
            {
                serverSocketv4 = new UdpClient(new IPEndPoint(IPAddress.Any, portNumber));
            }
            catch
            {
                serverSocketv4 = null;
            }
            if (serverSocketv6 != null)
            {
                serverSocketv6.BeginReceive(HandleReceive, serverSocketv6);
            }
            if (serverSocketv4 != null)
            {
                serverSocketv4.BeginReceive(HandleReceive, serverSocketv4);
            }
            while (serverSocketv4 != null || serverSocketv6 != null)
            {
                if (error)
                {
                    break;
                }
                SendClientsMeshState();
                System.Threading.Thread.Sleep(10000);
            }
            Shutdown();
        }

        private List<Guid> removeList = new List<Guid>();
        private List<byte[]> clientBytes = new List<byte[]>();
        private void SendClientsMeshState()
        {
            lock (clients)
            {
                byte[] sendGuidBytes = GetConnectedGuidBytes();
                clientBytes.Clear();
                foreach (UdpPeer client in clients.Values)
                {
                    clientBytes.Add(client.GetClientEndpointMessage());
                }
                foreach (KeyValuePair<Guid, UdpPeer> client in clients)
                {
                    if ((client.Value.lastReceiveTime + CLIENT_TIMEOUT) < DateTime.UtcNow.Ticks)
                    {
                        removeList.Add(client.Key);
                        continue;
                    }
                    if (serverSocketv4 != null && client.Value.usev4)
                    {
                        UdpMeshCommon.Send(serverSocketv4, sendGuidBytes, client.Value.contactV4);
                        foreach (byte[] clientByte in clientBytes)
                        {
                            UdpMeshCommon.Send(serverSocketv4, clientByte, client.Value.contactV4);
                        }
                    }
                    if (serverSocketv6 != null && client.Value.usev6)
                    {
                        UdpMeshCommon.Send(serverSocketv6, sendGuidBytes, client.Value.contactV6);
                        foreach (byte[] clientByte in clientBytes)
                        {
                            UdpMeshCommon.Send(serverSocketv6, clientByte, client.Value.contactV6);
                        }
                    }
                }
                foreach (Guid client in removeList)
                {
                    clients.Remove(client);
                    connectedGuidBytes = null;
                }
                removeList.Clear();
            }
        }

        private void HandleReceive(IAsyncResult ar)
        {
            UdpClient receiveClient = (UdpClient)ar.AsyncState;
            try
            {
                IPEndPoint receiveAddr = null;
                byte[] receiveBytes = receiveClient.EndReceive(ar, ref receiveAddr);
                if (receiveBytes.Length >= 24)
                {
                    UdpMeshCommon.ProcessBytes(receiveBytes, receiveAddr, callbacks);
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
                Console.WriteLine("Error restarting receive: " + e);
                error = true;
            }
        }



        public void Shutdown()
        {
            if (serverSocketv4 != null)
            {
                serverSocketv4.Close();
            }
            if (serverSocketv6 != null)
            {
                serverSocketv6.Close();
            }
            serverSocketv4 = null;
            serverSocketv6 = null;
            runTask = null;
        }

        private byte[] tempClientAddress4 = new byte[4];
        private byte[] tempClientAddress6 = new byte[16];
        private byte[] tempClientPort = new byte[2];
        private void ClientReport(byte[] inputData, Guid guid, IPEndPoint endpoint)
        {
            List<IPEndPoint> newEndpoints = new List<IPEndPoint>();
            lock (tempClientPort)
            {
                int readPos = 24;
                if (inputData.Length - readPos < 1)
                {
                    return;
                }
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
                    newEndpoints.Add(new IPEndPoint(ip, port));
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
                    newEndpoints.Add(new IPEndPoint(ip, port));
                    readPos += 2;
                }
                lock (clients)
                {
                    if (!clients.ContainsKey(guid))
                    {
                        clients.Add(guid, new UdpPeer(guid));
                        connectedGuidBytes = null;
                    }
                    UdpPeer client = clients[guid];
                    if (UdpMeshCommon.IsIPv4(endpoint.Address))
                    {
                        client.usev4 = true;
                        client.contactV4 = endpoint;
                    }
                    if (UdpMeshCommon.IsIPv6(endpoint.Address))
                    {
                        client.usev6 = true;
                        client.contactV6 = endpoint;
                    }
                    client.AddRemoteEndpoint(endpoint);
                    foreach (IPEndPoint tempEndPoint in newEndpoints)
                    {
                        client.AddRemoteEndpoint(tempEndPoint);
                    }
                    client.lastReceiveTime = DateTime.UtcNow.Ticks;
                }
            }
        }

        private byte[] tempType = new byte[4];
        private byte[] tempGuid = new byte[16];
        private byte[] relayHeader = UdpMeshCommon.GetPayload(-3, null);
        private void RelayMessage(byte[] inputBytes, Guid client, IPEndPoint endPoint)
        {
            //A valid message must contain 2 headers (24 bytes) and a destination GUID (16 bytes)
            if (inputBytes.Length < 64)
            {
                return;
            }

            //Make sure client isn't relaying control messages
            lock (tempType)
            {
                Array.Copy(inputBytes, 60, tempType, 0, 4);
                UdpMeshCommon.FlipEndian(ref tempType);
                int relayType = BitConverter.ToInt32(tempType, 0);
                if (relayType < 0)
                {
                    return;
                }
            }
            UdpPeer peer;
            lock (tempGuid)
            {
                Array.Copy(inputBytes, 24, tempGuid, 0, 16);
                Guid destinationGuid = new Guid(tempGuid);
                clients.TryGetValue(destinationGuid, out peer);
            }
            //Make sure client is connected
            if (peer == null)
            {
                return;
            }
            Array.Copy(relayHeader, 0, inputBytes, 0, 24);
            if (peer.usev6)
            {
                UdpMeshCommon.Send(serverSocketv6, inputBytes, peer.contactV6);
                return;
            }
            if (peer.usev4)
            {
                UdpMeshCommon.Send(serverSocketv4, inputBytes, peer.contactV4);
                return;
            }
        }

        private void ClientExternalReport(byte[] inputBytes, Guid guid, IPEndPoint iPEndPoint)
        {
            if (inputBytes.Length != 47 && inputBytes.Length != 59)
            {
                return;
            }
            byte type = inputBytes[40];
            Guid onBehalfOf;
            lock (tempGuid)
            {
                Array.Copy(inputBytes, 24, tempGuid, 0, 16);
                onBehalfOf = new Guid(tempGuid);
            }
            lock (clients)
            {
                UdpPeer peer;
                if (clients.TryGetValue(onBehalfOf, out peer))
                {
                    IPAddress addr;
                    int port;
                    if (inputBytes.Length == 47 && type == 4)
                    {
                        lock (tempClientPort)
                        {
                            Array.Copy(inputBytes, 41, tempClientAddress4, 0, 4);
                            addr = new IPAddress(tempClientAddress4);
                            Array.Copy(inputBytes, 45, tempClientPort, 0, 2);
                            UdpMeshCommon.FlipEndian(ref tempClientPort);
                            port = BitConverter.ToUInt16(tempClientPort, 0);
                        }
                        IPEndPoint endPoint = new IPEndPoint(addr, port);
                        peer.AddRemoteEndpoint(endPoint);
                    }
                    if (inputBytes.Length == 59 && type == 6)
                    {
                        lock (tempClientPort)
                        {
                            Array.Copy(inputBytes, 41, tempClientAddress6, 0, 16);
                            addr = new IPAddress(tempClientAddress6);
                            Array.Copy(inputBytes, 57, tempClientPort, 0, 2);
                            UdpMeshCommon.FlipEndian(ref tempClientPort);
                            port = BitConverter.ToUInt16(tempClientPort, 0);
                        }
                        IPEndPoint endPoint = new IPEndPoint(addr, port);
                        peer.AddRemoteEndpoint(endPoint);
                    }
                }
            }
        }

        private byte[] connectedGuidBytes;
        private byte[] GetConnectedGuidBytes()
        {
            if (connectedGuidBytes == null)
            {
                lock (clients)
                {
                    connectedGuidBytes = new byte[16 * clients.Count];
                    int writepos = 0;
                    foreach (Guid guid in clients.Keys)
                    {
                        Array.Copy(guid.ToByteArray(), 0, connectedGuidBytes, writepos, 16);
                        writepos = writepos + 16;
                    }
                    connectedGuidBytes = UdpMeshCommon.GetPayload(-1, connectedGuidBytes);
                }
            }
            return connectedGuidBytes;
        }
    }
}