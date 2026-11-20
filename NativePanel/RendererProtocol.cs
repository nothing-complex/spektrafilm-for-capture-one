internal enum RendererStage : byte { SourceAccepted = 1, Preparing, Vulkan, Verifying, Sending }

internal static class RendererProtocol
{
    internal const int Version = 2;
    internal const int ReplyHeaderBytes = 16;
    internal const int Canceled = 3;
    internal const int Progress = 4;
}
