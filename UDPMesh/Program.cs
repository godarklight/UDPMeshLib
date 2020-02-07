using System;
using System.Net;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UDPMeshLib;

namespace UDPMesh
{
    public class MainClass
    {
        private static Dictionary<Guid, long> testTime = new Dictionary<Guid, long>();
        private const int SERVER_PORT = 6702;

        public static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Running in client mode");
                RunClient();
            }
            else
            {
                if (args[0] == "-s")
                {
                    Console.WriteLine("Running in server mode");
                    RunServer();
                }
            }
        }

        public static void RunServer()
        {
            UdpMeshServer udpms = new UdpMeshServer(SERVER_PORT, Console.WriteLine);
            udpms.Run();
            Console.ReadKey();
        }

        public static void RunClient()
        {
            Console.WriteLine("My Guid: " + UdpMeshCommon.GetMeshAddress());
            Console.WriteLine("Ip addresses:");
            IPAddress[] addresses = UdpMeshCommon.GetLocalIPAddresses();
            foreach (IPAddress ip in UdpMeshCommon.GetLocalIPAddresses())
            {
                Console.WriteLine("IP: " + ip + ", family: " + ip.AddressFamily);
            }
            IPAddress[] ipAddrs = Dns.GetHostAddresses("godarklight.info.tm");
            IPAddress ipAddr = IPAddress.None;
            IPAddress ipAddr6 = IPAddress.None;

            foreach (IPAddress ip in ipAddrs)
            {
                if (UdpMeshCommon.IsIPv4(ip))
                {
                    ipAddr = ip;
                }
                if (UdpMeshCommon.IsIPv6(ip))
                {
                    ipAddr6 = ip;
                }
            }
            if (ipAddr == IPAddress.None)
            {
                Console.WriteLine("Unable to lookup godarklight.info.tm v4");
            }
            else
            {
                Console.WriteLine("Connecting to " + ipAddr);
            }
            if (ipAddr6 == IPAddress.None)
            {
                Console.WriteLine("Unable to lookup godarklight.info.tm v6");
            }
            else
            {
                Console.WriteLine("Connecting to " + ipAddr6);
            }
            if (ipAddr == IPAddress.None && ipAddr6 == IPAddress.None)
            {
                return;
            }
            UdpMeshClient udpmc = new UdpMeshClient(new IPEndPoint(ipAddr, 6702), new IPEndPoint(ipAddr6, 6702), addresses, Console.WriteLine);
            udpmc.RegisterCallback(0, UpdateClientTest);
            Thread clientTask = udpmc.Start();
            while (true)
            {
                Thread.Sleep(5000);
                Console.WriteLine("Guids on server: ");
                var peers = udpmc.GetPeers();
                Console.Write("Us, " + UdpMeshCommon.GetMeshAddress() + ", Connected: ");
                if (udpmc.connectedv4)
                {
                    Console.Write("V4 ");
                }
                if (udpmc.connectedv6)
                {
                    Console.Write("V6 ");
                }
                Console.WriteLine();
                foreach (UdpPeer peer in peers)
                {
                    if (peer.guid == UdpMeshCommon.GetMeshAddress())
                    {
                        continue;
                    }
                    udpmc.SendMessageToClient(peer.guid, 0, null);
                    Console.Write(peer.guid + ", Connected on: ");
                    if (peer.usev4)
                    {
                        Console.Write("V4 ");
                    }
                    if (peer.usev6)
                    {
                        Console.Write("V6 ");
                    }
                    double latency4 = peer.latency4 / (double)TimeSpan.TicksPerMillisecond;
                    double latency6 = peer.latency6 / (double)TimeSpan.TicksPerMillisecond;
                    double offset = peer.offset / (double)TimeSpan.TicksPerSecond;
                    Console.Write("Latency v4 (ms): " + Math.Round(latency4, 2));
                    Console.Write(", Latency v6 (ms): " + Math.Round(latency6, 2));
                    Console.Write(", Offset (s): " + Math.Round(offset, 2));
                    long lastTest;
                    if (testTime.TryGetValue(peer.guid, out lastTest))
                    {
                        if ((DateTime.UtcNow.Ticks - lastTest) > (TimeSpan.TicksPerMinute / 2))
                        {
                            Console.Write(", LOST CONTACT");
                        }
                    }
                    else
                    {
                        Console.Write(", NO CONTACT");
                    }
                    Console.WriteLine();
                }
            }
        }

        private static void UpdateClientTest(byte[] inputData, int length, Guid client, IPEndPoint endpoint)
        {
            testTime[client] = DateTime.UtcNow.Ticks;
        }
    }
}
