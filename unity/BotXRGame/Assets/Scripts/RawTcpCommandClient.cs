using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Plain TCP client that sends newline-terminated text commands.
///
/// Exists because the robot's arm node is NOT a ros_tcp_endpoint. It opens its
/// own socket on port 10001 and reads lines like "SWEEP\n" - so the ROS-TCP
/// protocol frames we were sending could never be understood, whatever the
/// address or topic. Two correct programs speaking different protocols.
///
/// Connect and send happen off the main thread: a TCP connect to an
/// unreachable host blocks for the OS timeout, and doing that on Unity's main
/// thread freezes the headset for seconds - which in a headset reads as a
/// crash, not a network problem.
/// </summary>
public class RawTcpCommandClient
{
    private TcpClient client;
    private NetworkStream stream;
    private Thread worker;
    private volatile bool running;
    private volatile bool connected;
    private volatile string status = "idle";

    private readonly object sendLock = new object();
    private string pending;                 // one command; newest wins

    private string host;
    private int port;
    private float nextRetryAt;

    public bool Connected => connected;
    public string Status => status;
    public int SentCount { get; private set; }
    public string LastSent { get; private set; } = "-";

    public void Connect(string ipAddress, int tcpPort)
    {
        Disconnect();

        host = ipAddress;
        port = tcpPort;
        running = true;
        status = "connecting to " + host + ":" + port;

        worker = new Thread(Run) { IsBackground = true, Name = "ArmTcp" };
        worker.Start();
    }

    public void Disconnect()
    {
        running = false;
        connected = false;

        try { stream?.Close(); } catch { }
        try { client?.Close(); } catch { }

        stream = null;
        client = null;
        status = "disconnected";
    }

    /// <summary>
    /// Queue a command. Returns false if there is no live connection.
    ///
    /// Only the newest command is kept: the arm rejects anything arriving while
    /// it is busy, so a backlog would replay stale presses seconds later.
    /// </summary>
    public bool Send(string command)
    {
        if (!connected) return false;

        lock (sendLock) pending = command;
        return true;
    }

    private void Run()
    {
        while (running)
        {
            try
            {
                if (client == null || !client.Connected)
                {
                    // Throttle retries. A tight reconnect loop against a host
                    // that is not listening burns CPU on a battery device and
                    // fills the log with identical failures.
                    if (Environment.TickCount < nextRetryAt) { Thread.Sleep(50); continue; }

                    client = new TcpClient();
                    var result = client.BeginConnect(host, port, null, null);

                    if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
                    {
                        client.Close();
                        client = null;
                        status = "no answer at " + host + ":" + port;
                        nextRetryAt = Environment.TickCount + 2000;
                        continue;
                    }

                    client.EndConnect(result);
                    stream = client.GetStream();
                    connected = true;
                    status = "connected " + host + ":" + port;
                }

                string toSend = null;
                lock (sendLock)
                {
                    if (pending != null) { toSend = pending; pending = null; }
                }

                if (toSend != null)
                {
                    // Trailing newline is the framing his reader splits on -
                    // without it the command sits in his buffer forever and
                    // nothing happens, with no error on either side.
                    byte[] bytes = Encoding.UTF8.GetBytes(toSend + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();

                    SentCount++;
                    LastSent = toSend;
                    status = "sent " + toSend;
                }

                Thread.Sleep(10);
            }
            catch (Exception e)
            {
                connected = false;
                status = "error: " + e.Message;

                try { stream?.Close(); } catch { }
                try { client?.Close(); } catch { }
                stream = null;
                client = null;

                nextRetryAt = Environment.TickCount + 2000;
            }
        }
    }
}
