using bow.data;
using Google.FlatBuffers;

namespace KinematicTrees.Robotics;

public sealed record ImageFrame(
    string Source,
    ulong FrameNumber,
    int Width,
    int Height,
    byte[] Data,
    ulong CapturedUnixNs);

public sealed record ImageSummary(
    string Source,
    ulong FrameNumber,
    uint[] DataShape,
    CompressionFormat Compression,
    ImageType ImageType,
    ulong CapturedUnixNs,
    byte[] DataPrefix);

public static class Vision
{
    public static byte[] EncodeImageSample(ImageFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0) throw new ArgumentOutOfRangeException(nameof(frame), "width and height must be positive");
        if (frame.Data.Length != checked(frame.Width * frame.Height * 3)) throw new ArgumentException("RGB payload length does not match shape", nameof(frame));
        var builder = new FlatBufferBuilder(1024 + frame.Data.Length);
        var source = builder.CreateString(frame.Source);
        var data = ImageSample.CreateDataVectorBlock(builder, frame.Data);
        var shape = ImageSample.CreateDataShapeVector(builder, new uint[] { (uint)frame.Height, (uint)frame.Width, 3 });
        var sample = ImageSample.CreateImageSample(
            builder,
            sourceOffset: source,
            dataOffset: data,
            data_shapeOffset: shape,
            compression: CompressionFormat.RAW,
            image_type: ImageType.RGB,
            frame_number: frame.FrameNumber,
            designation: StereoDesignation.NONE,
            new_data_flag: true,
            pipeline: MediaPipeline.UNKNOWN,
            captured_unix_ns: frame.CapturedUnixNs);
        ImageSample.FinishImageSampleBuffer(builder, sample);
        return builder.SizedByteArray();
    }

    public static ImageSummary DecodeImageSampleSummary(byte[] payload, int prefixBytes = 16)
    {
        var buffer = new ByteBuffer(payload);
        if (!ImageSample.ImageSampleBufferHasIdentifier(buffer)) throw new ArgumentException("payload is not a VSM1 ImageSample", nameof(payload));
        var sample = ImageSample.GetRootAsImageSample(buffer);
        var data = sample.GetDataArray() ?? Array.Empty<byte>();
        var shape = sample.GetDataShapeArray() ?? Array.Empty<uint>();
        return new ImageSummary(
            sample.Source ?? string.Empty,
            sample.FrameNumber,
            shape,
            sample.Compression,
            sample.ImageType,
            sample.CapturedUnixNs,
            data.Take(Math.Min(prefixBytes, data.Length)).ToArray());
    }
}
