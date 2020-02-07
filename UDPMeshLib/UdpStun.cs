using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace UDPMeshLib
{
    public class UdpStun
    {
        private static IPEndPoint stunServer1v4;
        private static IPEndPoint stunServer2v4;
        private static IPEndPoint stunServer1v6;
        private static IPEndPoint stunServer2v6;
        public static Action<StunResult> callback;

        private static byte[] GetRequestBytes()
        {
            byte[] header = new byte[28];
            //Binding request, two bytes, 0x00 0x01
            header[0] = 0;
            header[1] = 1;
            //Length of request, two bytes, 0x00 0x08
            header[2] = 0;
            header[3] = 8;
            //RFC5389 magic cookie
            header[4] = 33;
            header[5] = 18;
            header[6] = 164;
            header[7] = 66;
            //Ask for CHANGE-REQUEST 0x00 0x03
            header[20] = 0;
            header[21] = 3;
            //CHANGE-REQUEST is 4 bytes
            header[22] = 0;
            header[23] = 4;
            //Don't change IP or Port
            header[24] = 0;
            header[25] = 0;
            header[26] = 0;
            header[27] = 0;
            return header;
        }

        private static Guid WriteGuid(byte[] header)
        {
            byte[] modifiedGuid = new byte[16];
            Guid request = Guid.NewGuid();
            Array.Copy(request.ToByteArray(), 4, header, 8, 12);
            Array.Copy(header, 4, modifiedGuid, 0, 16);
            return new Guid(modifiedGuid);
        }

        /// <summary>
        /// Sends a stun message to google
        /// </summary>
        /// <returns>Returns transaction ID's sent</returns>
        /// <param name="client">UDP Socket to send from</param>
        public static Guid[] RequestRemoteIPv4(UdpClient client)
        {
            byte[] header = GetRequestBytes();
            Guid[] retVal = new Guid[2];
            retVal[0] = Guid.Empty;
            retVal[1] = Guid.Empty;
            if (stunServer1v4 == null)
            {
                SetStunServer();
            }
            if (stunServer1v4 == null)
            {
                return retVal;
            }
            try
            {
                retVal[0] = WriteGuid(header);
                UdpMeshCommon.Send(client, header, stunServer1v4);
            }
            catch
            {
                //Don't care
            }
            try
            {
                retVal[1] = WriteGuid(header);
                UdpMeshCommon.Send(client, header, stunServer2v4);
            }
            catch
            {
                //Don't care
            }
            return retVal;
        }

        public static Guid[] RequestRemoteIPv6(UdpClient client)
        {
            byte[] header = GetRequestBytes();
            Guid[] retVal = new Guid[2];
            retVal[0] = Guid.Empty;
            retVal[1] = Guid.Empty;
            if (stunServer1v4 == null)
            {
                SetStunServer();
            }
            if (stunServer1v6 == null)
            {
                return retVal;
            }
            try
            {
                retVal[0] = WriteGuid(header);
                UdpMeshCommon.Send(client, header, stunServer1v6);
            }
            catch
            {
                //Don't care
            }
            try
            {
                retVal[1] = WriteGuid(header);
                UdpMeshCommon.Send(client, header, stunServer2v6);
            }
            catch
            {
                //Don't care
            }
            return retVal;
        }

        /// <summary>
        /// Processes a received stun message.
        /// </summary>
        /// <returns>Stun result</returns>
        /// <param name="inputData">Input raw stun message</param>
        public static StunResult ProcessStun(byte[] inputData, int inputDataLength)
        {
            StunResult retVal = new StunResult();
            if (inputDataLength < 20)
            {
                return retVal;
            }
            byte[] messageShortBytes = new byte[2];
            Array.Copy(inputData, 0, messageShortBytes, 0, 2);
            UdpMeshCommon.FlipEndian(ref messageShortBytes);
            int messageTypeCombined = BitConverter.ToInt16(messageShortBytes, 0);
            Array.Copy(inputData, 2, messageShortBytes, 0, 2);
            UdpMeshCommon.FlipEndian(ref messageShortBytes);
            int messageLength = BitConverter.ToInt16(messageShortBytes, 0);
            byte[] messageGuidBytes = new byte[16];
            Array.Copy(inputData, 4, messageGuidBytes, 0, 16);
            retVal.messageGuid = new Guid(messageGuidBytes);
            int bytesToRead = messageLength;
            while (bytesToRead > 0)
            {
                if (bytesToRead < 4)
                {
                    return retVal;
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
                        retVal.port = BitConverter.ToUInt16(messageShortBytes, 0);
                        byte[] ipBytes = new byte[4];
                        Array.Copy(attrBytes, 4, ipBytes, 0, 4);
                        retVal.remoteAddr = new IPAddress(ipBytes);
                        retVal.success = true;
                    }
                    if (attrType == 1 && attrBytes[1] == 2 && attrLength == 20)
                    {
                        Array.Copy(attrBytes, 2, messageShortBytes, 0, 2);
                        UdpMeshCommon.FlipEndian(ref messageShortBytes);
                        retVal.port = BitConverter.ToUInt16(messageShortBytes, 0);
                        byte[] ipBytes = new byte[16];
                        Array.Copy(attrBytes, 4, ipBytes, 0, 16);
                        retVal.remoteAddr = new IPAddress(ipBytes);
                        retVal.success = true;
                    }
                    if (attrType == 32 && attrBytes[1] == 1 && attrLength == 8)
                    {
                        Array.Copy(attrBytes, 2, messageShortBytes, 0, 2);
                        //Xor with magic cookie
                        XorBytes(messageShortBytes, messageGuidBytes);
                        UdpMeshCommon.FlipEndian(ref messageShortBytes);
                        retVal.port = BitConverter.ToUInt16(messageShortBytes, 0);
                        byte[] ipBytes = new byte[4];
                        Array.Copy(attrBytes, 4, ipBytes, 0, 4);
                        //Xor with magic cookie
                        XorBytes(ipBytes, messageGuidBytes);
                        retVal.remoteAddr = new IPAddress(ipBytes);
                        retVal.success = true;
                    }
                    if (attrType == 32 && attrBytes[1] == 2 && attrLength == 20)
                    {
                        Array.Copy(attrBytes, 2, messageShortBytes, 0, 2);
                        //Xor with magic cookie
                        XorBytes(messageShortBytes, messageGuidBytes);
                        UdpMeshCommon.FlipEndian(ref messageShortBytes);
                        retVal.port = BitConverter.ToUInt16(messageShortBytes, 0);
                        byte[] ipBytes = new byte[16];
                        Array.Copy(attrBytes, 4, ipBytes, 0, 16);
                        //Xor with magic cookie
                        XorBytes(ipBytes, messageGuidBytes);
                        retVal.remoteAddr = new IPAddress(ipBytes);
                        retVal.success = true;
                    }
                }
            }
            if (callback != null)
            {
                callback(retVal);
            }
            return retVal;
        }

        private static void XorBytes(byte[] input, byte[] pattern)
        {
            if (input == null || pattern == null)
            {
                return;
            }
            for (int i = 0; i < input.Length; i++)
            {
                int patternPos = i % pattern.Length;
                input[i] = (byte)(input[i] ^ pattern[patternPos]);
            }
        }

        private static void SetStunServer()
        {
            IPAddress[] addrs1 = Dns.GetHostAddresses("stun1.l.google.com");
            IPAddress[] addrs2 = Dns.GetHostAddresses("stun2.l.google.com");
            foreach (IPAddress addr1 in addrs1)
            {
                if (UdpMeshCommon.IsIPv4(addr1))
                {
                    stunServer1v4 = new IPEndPoint(addr1, 19302);
                    break;
                }
            }
            foreach (IPAddress addr2 in addrs2)
            {
                if (UdpMeshCommon.IsIPv4(addr2))
                {
                    stunServer2v4 = new IPEndPoint(addr2, 19302);
                    break;
                }
            }
            foreach (IPAddress addr1 in addrs1)
            {
                if (UdpMeshCommon.IsIPv6(addr1))
                {
                    stunServer1v6 = new IPEndPoint(addr1, 19302);
                    break;
                }
            }
            foreach (IPAddress addr2 in addrs2)
            {
                if (UdpMeshCommon.IsIPv6(addr2))
                {
                    stunServer2v6 = new IPEndPoint(addr2, 19302);
                    break;
                }
            }
        }

        public class StunResult
        {
            public Guid messageGuid = Guid.Empty;
            public bool success = false;
            public IPAddress remoteAddr = IPAddress.None;
            public int port = 0;
        }
    }
}