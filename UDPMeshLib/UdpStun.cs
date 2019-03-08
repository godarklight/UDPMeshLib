using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace UDPMeshLib
{
    public class UdpStun
    {
        private static IPEndPoint stunServer1;
        private static IPEndPoint stunServer2;
        public static Guid[] RequestRemoteIP(UdpClient client)
        {
            Guid[] retVal = new Guid[2];
            retVal[0] = Guid.Empty;
            retVal[1] = Guid.Empty;
            if (stunServer1 == null)
            {
                SetStunServer();
            }
            if (stunServer1 == null)
            {
                return retVal;
            }
            byte[] header = new byte[28];
            //Binding request, two bytes, 0x00 0x01
            header[0] = 0;
            header[1] = 1;
            //Length of request, two bytes, 0x00 0x08
            header[2] = 0;
            header[3] = 8;
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
            try
            {
                Guid request = Guid.NewGuid();
                retVal[0] = request;
                Array.Copy(request.ToByteArray(), 0, header, 4, 16);
                client.Send(header, header.Length, stunServer1);
            }
            catch
            {
                //Don't care
            }
            try
            {
                Guid request = Guid.NewGuid();
                retVal[1] = request;
                Array.Copy(request.ToByteArray(), 0, header, 4, 16);
                UdpMeshCommon.Send(client, header, stunServer1);
            }
            catch
            {
                //Don't care
            }
            return retVal;
        }

        private static void SetStunServer()
        {
            IPAddress[] addrs1 = Dns.GetHostAddresses("stun1.l.google.com");
            IPAddress[] addrs2 = Dns.GetHostAddresses("stun2.l.google.com");
            foreach (IPAddress addr1 in addrs1)
            {
                if (UdpMeshCommon.IsIPv4(addr1))
                {
                    stunServer1 = new IPEndPoint(addr1, 19302);
                    break;
                }
            }
            foreach (IPAddress addr2 in addrs2)
            {
                if (UdpMeshCommon.IsIPv4(addr2))
                {
                    stunServer2 = new IPEndPoint(addr2, 19302);
                    break;
                }
            }
        }
    }
}