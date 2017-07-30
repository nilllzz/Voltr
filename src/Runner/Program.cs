using NetVoltr;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Runner
{
    class Program
    {
        static void Main(string[] args)
        {
            Task.Run(() => Do());
            SpinWait.SpinUntil(() => false);
        }

        private static async void Do()
        {
            var voltr = new Voltr();
            await voltr.Open();

            var channel = voltr.GetChannel("drive");
            channel.MessageReceived += Voltr_MessageReceived;
            await channel.Subscribe();

            while (true)
            {
                var message = Console.ReadLine();
                await channel.Publish(message);
            }
        }

        private static void Voltr_MessageReceived(Channel source, string cid, byte[] messageBytes)
        {
            var message = source.Parent.Encoding.GetString(messageBytes);
            Console.WriteLine($"Received message from {cid}: {message}");
        }
    }
}
