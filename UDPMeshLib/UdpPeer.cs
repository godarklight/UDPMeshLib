using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace UDPMeshLib
{
    public class UdpPeer
    {
        /// <summary>
        /// The GUID of the client
        /// </summary>
        public readonly Guid guid;
        /// <summary>
        /// Time in UTC
        /// </summary>
        public long lastReceiveTime;
        /// <summary>
        /// Time in ticks
        /// </summary>
        public long latency4 = long.MaxValue;
        /// <summary>
        /// Time in ticks. Negative means their clock is slower relative to us.
        /// </summary>
        public long offset;
        /// <summary>
        /// We have received an IPv4 message
        /// </summary>
        public long latency6 = long.MaxValue;
        /// <summary>
        /// We have received an IPv4 message
        /// </summary>
        public bool usev4 = false;
        /// <summary>
        /// The endpoint we received an IPv4 message from
        /// </summary>
        public IPEndPoint contactV4;
        /// <summary>
        /// We have received an IPv6 message
        /// </summary>
        public bool usev6 = false;
        /// <summary>
        /// The endpoint we received an IPv6 message from
        /// </summary>
        public IPEndPoint contactV6;
        /// <summary>
        /// The endpoints that the server knows about
        /// </summary>
        public List<IPEndPoint> remoteEndpoints = new List<IPEndPoint>();
        private byte[] cachedData;

        public UdpPeer(Guid guid)
        {
            this.guid = guid;
        }

        /// <summary>
        /// Adds the remote endpoint that we know about
        /// </summary>
        /// <param name="endPoint">The endpoint</param>
        public void AddRemoteEndpoint(IPEndPoint endPoint)
        {
            foreach (IPEndPoint remoteEndpoint in remoteEndpoints)
            {
                if (remoteEndpoint.Equals(endPoint))
                {
                    return;
                }
            }
            remoteEndpoints.Add(endPoint);
            cachedData = null;
        }

        /// <summary>
        /// Sent TO the server
        /// </summary>
        internal byte[] GetServerEndpointMessage()
        {
            if (cachedData == null)
            {
                List<IPEndPoint> v4end = new List<IPEndPoint>();
                List<IPEndPoint> v6end = new List<IPEndPoint>();
                foreach (IPEndPoint endPoint in remoteEndpoints)
                {
                    if (endPoint.AddressFamily == AddressFamily.InterNetwork)
                    {
                        v4end.Add(endPoint);
                    }
                    if (endPoint.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        v6end.Add(endPoint);
                    }
                }
                cachedData = new byte[2 + 6 * v4end.Count + 18 * v6end.Count];
                cachedData[0] = (Byte)v4end.Count;
                int writepos = 1;
                foreach (IPEndPoint endPoint in v4end)
                {
                    byte[] addrBytes = endPoint.Address.GetAddressBytes();
                    Array.Copy(addrBytes, 0, cachedData, writepos, 4);
                    writepos += 4;
                    byte[] portBytes = BitConverter.GetBytes((ushort)endPoint.Port);
                    UdpMeshCommon.FlipEndian(ref portBytes);
                    Array.Copy(portBytes, 0, cachedData, writepos, 2);
                    writepos += 2;
                }
                cachedData[writepos] = (Byte)v6end.Count;
                writepos++;
                foreach (IPEndPoint endPoint in v6end)
                {
                    byte[] addrBytes = endPoint.Address.GetAddressBytes();
                    Array.Copy(addrBytes, 0, cachedData, writepos, 16);
                    writepos += 16;
                    byte[] portBytes = BitConverter.GetBytes((ushort)endPoint.Port);
                    UdpMeshCommon.FlipEndian(ref portBytes);
                    Array.Copy(portBytes, 0, cachedData, writepos, 2);
                    writepos += 2;

                }
                cachedData = UdpMeshCommon.GetPayload(-101, cachedData);
            }
            return cachedData;
        }

        /// <summary>
        /// Send FROM the server
        /// </summary>
        internal byte[] GetClientEndpointMessage()
        {
            if (cachedData == null)
            {
                List<IPEndPoint> v4end = new List<IPEndPoint>();
                List<IPEndPoint> v6end = new List<IPEndPoint>();
                foreach (IPEndPoint endPoint in remoteEndpoints)
                {
                    if (endPoint.AddressFamily == AddressFamily.InterNetwork)
                    {
                        v4end.Add(endPoint);
                    }
                    if (endPoint.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        v6end.Add(endPoint);
                    }
                }
                cachedData = new byte[18 + 6 * v4end.Count + 18 * v6end.Count];
                Array.Copy(guid.ToByteArray(), cachedData, 16);
                cachedData[16] = (Byte)v4end.Count;
                int writepos = 17;
                foreach (IPEndPoint endPoint in v4end)
                {
                    byte[] addrBytes = endPoint.Address.GetAddressBytes();
                    Array.Copy(addrBytes, 0, cachedData, writepos, 4);
                    writepos += 4;
                    byte[] portBytes = BitConverter.GetBytes((ushort)endPoint.Port);
                    UdpMeshCommon.FlipEndian(ref portBytes);
                    Array.Copy(portBytes, 0, cachedData, writepos, 2);
                    writepos += 2;
                }
                cachedData[writepos] = (Byte)v6end.Count;
                writepos++;
                foreach (IPEndPoint endPoint in v6end)
                {
                    byte[] addrBytes = endPoint.Address.GetAddressBytes();
                    Array.Copy(addrBytes, 0, cachedData, writepos, 16);
                    writepos += 16;
                    byte[] portBytes = BitConverter.GetBytes((ushort)endPoint.Port);
                    UdpMeshCommon.FlipEndian(ref portBytes);
                    Array.Copy(portBytes, 0, cachedData, writepos, 2);
                    writepos += 2;

                }
                cachedData = UdpMeshCommon.GetPayload(-2, cachedData);
            }
            return cachedData;
        }
    }
}
