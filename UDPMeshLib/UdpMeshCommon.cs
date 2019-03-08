using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
namespace UDPMeshLib
{
    public class UdpMeshCommon
    {
        private static byte[] magicHeader;
        public static byte[] GetMagicHeader()
        {
            if (magicHeader == null)
            {
                magicHeader = new byte[4] { 85, 68, 80, 77 };
            }
            return magicHeader;
        }

        private static Guid meshAddress;
        private static byte[] meshAddressBytes;
        public static Guid GetMeshAddress()
        {
            if (meshAddressBytes == null)
            {
                meshAddress = Guid.NewGuid();
                meshAddressBytes = meshAddress.ToByteArray();
            }
            return meshAddress;
        }
        public static byte[] GetMeshAddressBytes()
        {
            if (meshAddressBytes == null)
            {
                meshAddress = Guid.NewGuid();
                meshAddressBytes = meshAddress.ToByteArray();
            }
            return meshAddressBytes;
        }

        public static IPAddress[] GetLocalIPAddresses()
        {
            List<IPAddress> retVal = new List<IPAddress>();
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface adapter in nics)
            {
                if (adapter.OperationalStatus == OperationalStatus.Up)
                {
                    foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            byte[] addressBytes = unicast.Address.GetAddressBytes();
                            //Ignore fe80 address
                            if (addressBytes[0] == 254 && addressBytes[1] == 128)
                            {
                                continue;
                            }
                        }
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork || unicast.Address.AddressFamily == AddressFamily.InterNetworkV6)
                            retVal.Add(unicast.Address);
                    }
                }
            }
            return retVal.ToArray();
        }

        public static void FlipEndian(ref byte[] input)
        {
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(input);
            }
        }

        public static byte[] GetPayload(int type, byte[] data)
        {
            byte[] retVal;
            if (data == null)
            {
                retVal = new byte[24];
            }
            else
            {
                retVal = new byte[24 + data.Length];
            }
            Array.Copy(GetMagicHeader(), 0, retVal, 0, 4);
            Array.Copy(GetMeshAddressBytes(), 0, retVal, 4, 16);
            byte[] tempBytes = BitConverter.GetBytes(type);
            FlipEndian(ref tempBytes);
            Array.Copy(tempBytes, 0, retVal, 20, 4);
            if (data != null)
            {
                Array.Copy(data, 0, retVal, 24, data.Length);
            }
            return retVal;
        }

        private static byte[] tempGuidBytes = new byte[16];
        private static byte[] tempType = new byte[4];
        private static byte[] magicBytes = UdpMeshCommon.GetMagicHeader();
        public static void ProcessBytes(byte[] inputData, IPEndPoint endpoint, Dictionary<int, Action<byte[], Guid, IPEndPoint>> callbacks)
        {
            if (inputData.Length >= 2)
            {
                if (inputData[0] == 1 && inputData[1] == 1)
                {
                    if (callbacks.ContainsKey(int.MinValue))
                    {
                        callbacks[int.MinValue](inputData, Guid.Empty, endpoint);
                    }
                }
            }
            for (int i = 0; i < magicBytes.Length; i++)
            {
                if (magicBytes[i] != inputData[i])
                {
                    return;
                }
            }
            lock (tempGuidBytes)
            {
                int bytesToProcess = inputData.Length - 4;
                if (bytesToProcess < 16)
                {
                    return;
                }
                bytesToProcess -= 16;
                Array.Copy(inputData, 4, tempGuidBytes, 0, 16);
                Guid recvGuid = new Guid(tempGuidBytes);
                if (bytesToProcess < 4)
                {
                    return;
                }
                Array.Copy(inputData, 20, tempType, 0, 4);
                FlipEndian(ref tempType);
                int dataType = BitConverter.ToInt32(tempType, 0);
                //Console.WriteLine("Processed " + inputData.Length + " type: " + dataType);
                if (callbacks.ContainsKey(dataType))
                {
                    callbacks[dataType](inputData, recvGuid, endpoint);
                }
            }
        }

        //Cant use addr.IsIPv4MappedToIPv6 - doesn't exist in lower .NET versions
        private static byte[] v4Bytes = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 255 };
        public static bool IsIPv4(IPAddress addr)
        {
            if (addr.AddressFamily == AddressFamily.InterNetwork)
            {
                return true;
            }
            bool isv4 = true;
            byte[] addrBytes = addr.GetAddressBytes();
            if (addrBytes.Length == 16)
            {
                for (int i = 0; i < 12; i++)
                {
                    if (addrBytes[i] != v4Bytes[i])
                    {
                        isv4 = false;
                    }
                }
            }
            if (isv4)
            {
                return true;
            }
            return false;
        }

        public static bool IsIPv6(IPAddress addr)
        {
            if (addr.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }
            bool isv4 = true;
            byte[] addrBytes = addr.GetAddressBytes();
            if (addrBytes.Length == 16)
            {
                for (int i = 0; i < 12; i++)
                {
                    if (addrBytes[i] != v4Bytes[i])
                    {
                        isv4 = false;
                    }
                }
            }
            if (isv4)
            {
                return false;
            }
            return true;
        }

        public static void Send(UdpClient socket, byte[] data, IPEndPoint endPoint)
        {
            try
            {
                socket.Send(data, data.Length, endPoint);
            }
            catch
            {
                //Don't care.
            }
        }
    }
}
