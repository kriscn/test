using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net;
using DotNext.Buffers;
using System.Buffers;
using System.Diagnostics;
using System;
using static WebApplication1.TcpServer.Clients.RadarFrameMessage;

namespace WebApplication1.TcpServer.Clients;

//public struct RadarPoint
//{
//    public float X;
//    public float Y;
//    public float Z;
//}
public class RadarScanData
{
    public RadarScanData(UInt16 pitchAngleStart, UInt16 pitchAngleEnd, RadarFrameMessagePoint[] points)
    {
        PitchAngleStart = pitchAngleStart;
        PitchAngleEnd = pitchAngleEnd;
        Points = points;
    }
    public ushort PitchAngleStart { get; }
    public ushort PitchAngleEnd { get; }
    public RadarFrameMessagePoint[] Points { get; }
}
public class RadarScanDataCollection
{
    public RadarScanDataCollection()
    {
        _scanDataList = new List<RadarScanData>();
    }
    public List<RadarScanData> _scanDataList;
    public IReadOnlyList<RadarScanData> ScanDataList => _scanDataList;
    public void AddData(RadarFrameMessage message)
    {

    }
}


public enum RadarRunningState
{
    None,
    Stopped,
    Scanning,
    Backing,
    ReadyA,
    ReadyB,
    ReadyC,
    ReadyD,
    ReadyE,
    Completed
}

public class RadarCommandMessage
{
    private RadarCommandMessage(RadarCommandMessageType type, RadarCommandMessageContent? content = null)
    {
        Type = type;
        Content = content;
    }
    public RadarCommandMessageType Type { get;  }
    public RadarCommandMessageContent? Content { get; }

    public static RadarCommandMessage Start => new RadarCommandMessage(RadarCommandMessageType.StartRadar);
    public static RadarCommandMessage Stop => new RadarCommandMessage(RadarCommandMessageType.StopRadar);
    public static RadarCommandMessage ScanContinous => new RadarCommandMessage(RadarCommandMessageType.ScanContinous);
    public static RadarCommandMessage ScanSingle => new RadarCommandMessage(RadarCommandMessageType.ScanSingle);
    public static RadarCommandMessage SelectGroupESingle => new RadarCommandMessage(RadarCommandMessageType.SelectGroupESingle);

    public static RadarCommandMessage DownloadGroupE(UInt16 startAngle, UInt16 endAngle, Byte bufferPos, UInt16 speed) => 
        new RadarCommandMessage(RadarCommandMessageType.DownloadGroupE, new RadarCommandMessageContentDownloadE() { 
            StartAngle = startAngle, EndAngle=endAngle, BufferPos = bufferPos, Speed = speed});
}

public enum RadarCommandMessageType
{
    StartRadar,
    StopRadar,

    ScanContinous,
    ScanSingle,
    SelectGroupASingle,
    SelectGroupBSingle,
    SelectGroupCSingle,
    SelectGroupDSingle,
    SelectGroupESingle,
    SelectGroupAContinous,
    SelectGroupBContinous,
    SelectGroupCContinous,
    SelectGroupDContinous,
    SelectGroupEContinous,
    DownloadGroupE
}

public abstract class RadarCommandMessageContent
{
}

public class RadarCommandMessageContentDownloadE : RadarCommandMessageContent
{
    public UInt16 StartAngle { get; set; }
    public UInt16 EndAngle { get; set; }
    public Byte BufferPos { get; set; }
    public UInt16 Speed { get; set; }
}

public class MiTcpClient
{
    private readonly ILogger _logger;
    private Socket? _clientSocket;
    private NetworkStream? _stream;
    private IPEndPoint? _endPoint;
    private const int SingleMessageGapDelay = 1000;
    //private readonly IMessageProtocol<RadarFrameMessage, RadarCommandMessage> _protocol;
    private static RadarFrameMessageSerializer RadarFrameMessageSerializer = new RadarFrameMessageSerializer();
    private static RadarCommandMessageSerializer RadarCommandMessageSerializer = new RadarCommandMessageSerializer();

    public MiTcpClient(ILogger<MiTcpClient> logger)
    {
        _logger = logger;
        //_protocol = 
    }

    public bool IsConnected => _clientSocket?.Connected ?? false;

    public async ValueTask ConnectAsync(IPEndPoint endPoint, CancellationToken token)
    {
        _endPoint = endPoint;
        //if (clientSocket != null) throw new InvalidOperationException("Can not connect. Socket already created. ");
        if (IsConnected) return;
        try
        {
            if (_clientSocket == null) _clientSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await _clientSocket.ConnectAsync(_endPoint, token);
            _stream = new NetworkStream(_clientSocket);
            _pipeReader = PipeReader.Create(_stream);
            _pipeWriter = PipeWriter.Create(_stream);

            //var context = new MiTcpClientConnectionContext(_endPoint, PipeReader.Create(_stream), PipeWriter.Create(_stream), () => Release());
            //_connectionHandler?.OnConnectedAsync(context);

            await OnConnectedAsync(token);
        }
        catch (Exception)
        {
            Release();
            throw;
        }
    }

    PipeReader? _pipeReader;
    PipeWriter? _pipeWriter;

    private async Task OnFrameMessageReceivedAsync(RadarFrameMessage frameMessage)
    {
        Debug.Print($"Received. ============================ S3={frameMessage.S3}, S4={frameMessage.S4}");
        Interlocked.Exchange(ref _lastReceiveTime, DateTime.Now.Ticks);
        var lastRunningState = _radarRunningState;
        _radarRunningState = frameMessage.GetRunningState();

        if(_radarRunningState == RadarRunningState.Scanning && lastRunningState != RadarRunningState.Scanning)
        {
            _lastRadarData = null;
            _radarDataList.Clear();
        }
        if (_radarRunningState == RadarRunningState.Scanning)
        {
            _radarDataList.Add(frameMessage);
        }
        if (_radarRunningState != RadarRunningState.Scanning && lastRunningState == RadarRunningState.Scanning)
        {
            _lastRadarData = _radarDataList;
            _radarDataList = new List<RadarFrameMessage>();
        }
    }

    private RadarRunningState _radarRunningState = RadarRunningState.None;
    public RadarRunningState RadarRunningState => _radarRunningState;

    private bool _isRadarRunning = false;
    public bool IsRadarRunning => DateTime.Now.Ticks - Interlocked.Read(ref _lastReceiveTime) < 10000 * 1000;
    private long _lastReceiveTime = 0;

    private List<RadarFrameMessage> _radarDataList = new List<RadarFrameMessage>();

    private IReadOnlyList<RadarFrameMessage>? _lastRadarData;
    public IReadOnlyList<RadarFrameMessage>? LastRadarData => _lastRadarData;

    private async Task OnConnectedAsync(CancellationToken token)
    {
        _logger.LogTrace("Server {IpAddress} connected. ", _endPoint);
        try
        {
            var reader = _pipeReader;
            var writer = _pipeWriter;

            var isBufferEmpty = true;
            SequencePosition examined;
            SequencePosition consumed;
            while (!token.IsCancellationRequested)
            {
                ReadResult result;
                if (isBufferEmpty)
                {
                    result = await reader.ReadAsync(default);
                }
                else
                {
                    using var cts = new CancellationTokenSource(SingleMessageGapDelay);
                    try
                    {
                        result = await reader.ReadAsync(cts.Token);
                    }
                    catch (OperationCanceledException ex)
                    {
                        if (ex.CancellationToken == cts.Token) throw new TimeoutException("MessageCancelledStatus");
                        throw;
                    }
                }
                if (result.IsCanceled)
                {
                    throw new TimeoutException("Message not complete timeout. ");
                }
                var buffer = result.Buffer;
                if (!buffer.IsEmpty)
                {
                    while (!token.IsCancellationRequested)
                    {
                        consumed = buffer.Start;
                        examined = buffer.End;
                        RadarFrameMessage request;
                        if (TryParseMessage(buffer, ref consumed, ref examined, out request))
                        {
                            await OnFrameMessageReceivedAsync(request);

                            if (consumed.Equals(buffer.End))
                            {
                                reader.AdvanceTo(buffer.End);
                                isBufferEmpty = true;
                                break;
                            }
                            if (examined.Equals(buffer.End))
                            {
                                reader.AdvanceTo(consumed, examined);
                                isBufferEmpty = false;
                                break;
                            }
                            buffer = buffer.Slice(consumed);
                        }
                        else
                        {
                            reader.AdvanceTo(buffer.Start, buffer.End);
                            isBufferEmpty = false;
                            break;
                        }
                    }
                }
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnConnectedAsync Error, {ErrorMessage} Connection ID={EndPoint}. ", ex.Message, _endPoint);
        }
        finally
        {
            _logger.LogTrace("Server {EndPoint} disconnected. ", _endPoint);
        }
    }

    private int _frameLength = 1020 * 6;
    private bool TryParseMessage(in ReadOnlySequence<byte> input, ref SequencePosition consumed, ref SequencePosition examined, out RadarFrameMessage message)
    {
        var reader = new SequenceReader<byte>(input);
        if (reader.Remaining < _frameLength)
        {
            message = default;
            return false;
        }
        var payload = input.Slice(reader.Position, _frameLength);
        message = RadarFrameMessageSerializer.Deserialize(payload);

        consumed = payload.End;
        examined = consumed;
        return true;
    }

    public async Task SendCommand(RadarCommandMessage command)
    {
        using (var requestWriter = new SequenceBuilder<byte>(2 * 1024))
        {
            RadarCommandMessageSerializer.Serialize(command, requestWriter);
            var buffer = requestWriter.ToReadOnlySequence();
            foreach (var memory in buffer)
            {
                _pipeWriter.Write(memory.Span);
            }
            await _pipeWriter.FlushAsync();
            await Task.Delay(100);
        }
    }


    //private async Task OnConnectedAsync(PipeReader reader, PipeWriter writer)
    //{
    //    _logger.LogTrace("{IpAddress} connected. ", _endPoint);
    //    try
    //    {
    //        var isBufferEmpty = true;
    //        SequencePosition examined;
    //        SequencePosition consumed;
    //        while (true)
    //        {
    //            ReadResult result;
    //            if (isBufferEmpty)
    //            {
    //                result = await reader.ReadAsync(default);
    //            }
    //            else
    //            {
    //                using var cts = new CancellationTokenSource(SingleMessageGapDelay);
    //                try
    //                {
    //                    result = await reader.ReadAsync(cts.Token);
    //                }
    //                catch (OperationCanceledException ex)
    //                {
    //                    if (ex.CancellationToken == cts.Token) throw new TimeoutException("MessageCancelledStatus");
    //                    throw;
    //                }
    //            }
    //            if (result.IsCanceled)
    //            {
    //                throw new TimeoutException("Message not complete timeout. ");
    //            }
    //            var buffer = result.Buffer;
    //            if (!buffer.IsEmpty)
    //            {
    //                while (true)
    //                {
    //                    consumed = buffer.Start;
    //                    examined = buffer.End;
    //                    TRequest request;
    //                    if (_protocol.TryParseMessage(buffer, ref consumed, ref examined, out request))
    //                    {
    //                        var response = await _messageHandler.ExecuteAsync(request, CancellationToken.None);
    //                        _protocol.WriteMessage(response, writer);
    //                        await writer.FlushAsync();

    //                        if (consumed.Equals(buffer.End))
    //                        {
    //                            reader.AdvanceTo(buffer.End);
    //                            isBufferEmpty = true;
    //                            break;
    //                        }
    //                        if (examined.Equals(buffer.End))
    //                        {
    //                            reader.AdvanceTo(consumed, examined);
    //                            isBufferEmpty = false;
    //                            break;
    //                        }
    //                        buffer = buffer.Slice(consumed);
    //                        //if (!consumed.Equals(buffer.End))
    //                        //{
    //                        //    throw new InvalidOperationException("Too many data after request. ");
    //                        //}
    //                    }
    //                    else
    //                    {
    //                        reader.AdvanceTo(buffer.Start, buffer.End);
    //                        isBufferEmpty = false;
    //                        break;
    //                    }
    //                }
    //            }
    //            if (result.IsCompleted)
    //            {
    //                break;
    //            }
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogError(ex, "OnConnectedAsync Error, {ErrorMessage} Connection ID={ConnectionID}. ", ex.Message, connection.ConnectionId);
    //    }
    //    finally
    //    {
    //        _logger.LogTrace("{ConnectionID} disconnected. ", connection.ConnectionId);
    //    }
    //}

    //public async ValueTask<TResponse> SendRequestAsync(TRequest request, CancellationToken token)
    //{
    //    _protocol.WriteMessage(request, _pipeWriter);
    //    await _pipeWriter.FlushAsync(token);
    //    while (true)
    //    {
    //        ReadResult result = await _pipeReader.ReadAsync(token);
    //        token.ThrowIfCancellationRequested();
    //        var buffer = result.Buffer;
    //        if (!buffer.IsEmpty)
    //        {
    //            var consumed = buffer.Start;
    //            var examined = buffer.End;
    //            TResponse response;
    //            if (_protocol.TryParseMessage(buffer, ref consumed, ref examined, out response))
    //            {
    //                _pipeReader.AdvanceTo(buffer.End);
    //                if (!consumed.Equals(buffer.End))
    //                {
    //                    throw new InvalidOperationException("Too many data after response. ");
    //                }
    //                return response;
    //            }
    //            _pipeReader.AdvanceTo(buffer.Start, buffer.End);
    //        }
    //        if (result.IsCompleted)
    //        {
    //            throw new InvalidOperationException("Connection broken. ");
    //        }
    //    }
    //}

    private void Release()
    {
        try { _stream?.Dispose(); } catch { }
        try { _clientSocket?.Close(); } catch { }
        try { _clientSocket?.Dispose(); } catch { }
        _stream = null;
        _clientSocket = null;
    }

    public async ValueTask DisconnectAsync(CancellationToken token)
    {
        try
        {
            var tcs = new TaskCompletionSource();
            using var e = new SocketAsyncEventArgs();
            token.Register(() => ((Socket)e.UserToken)?.Dispose());
            e.Completed += (s, e) => tcs.SetResult();

            try
            {
                if (_clientSocket.DisconnectAsync(e))
                {
                    await tcs.Task;
                }
                else
                {
                    return;
                }
            }
            catch (Exception)
            {
                throw;
            }
        }
        finally
        {
            Release();
        }
    }

    #region Dispose

    private bool _disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                Release();
            }

            // TODO: 释放未托管的资源(未托管的对象)并重写终结器
            // TODO: 将大型字段设置为 null
            _disposedValue = true;
        }
    }

    // // TODO: 仅当“Dispose(bool disposing)”拥有用于释放未托管资源的代码时才替代终结器
    // ~MiTcpClient()
    // {
    //     // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #endregion

    //#region Helpers

    //private static ValueTask WriteBuffer(Stream target, in SparseBufferWriter<byte> data, CancellationToken token)
    //{
    //    static async ValueTask WriteBufferAwaited(Stream ttarget, SparseBufferWriter<byte> ddata, CancellationToken token)
    //    {
    //        foreach (var segment in ddata)
    //        {
    //            await ttarget.WriteAsync(segment);
    //        }
    //    }
    //    if (data.IsSingleSegment)
    //    {
    //        return target.WriteAsync(data.First());
    //    }
    //    else
    //    {
    //        return WriteBufferAwaited(target, data, token);
    //    }
    //}

    //private static ValueTask WriteBuffer(Stream target, in ReadOnlySequence<byte> data)
    //{
    //    static async ValueTask WriteBufferAwaited(Stream ttarget, ReadOnlySequence<byte> ddata)
    //    {
    //        foreach (var segment in ddata)
    //        {
    //            await ttarget.WriteAsync(segment);
    //        }
    //    }
    //    if (data.IsSingleSegment)
    //    {
    //        return target.WriteAsync(data.First);
    //    }
    //    else
    //    {
    //        return WriteBufferAwaited(target, data);
    //    }
    //}

    //#endregion
}