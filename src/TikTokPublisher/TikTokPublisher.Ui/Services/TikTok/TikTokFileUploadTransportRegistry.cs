using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace TikTokPublisher.Ui.Services.TikTok;

internal enum TikTokFileUploadTransport
{
    CdpPathInjection,
    LocalPlaywright,
}

/// <summary>
/// Records how a page was opened so local Playwright pages do not inherit the
/// ConnectOverCDP file-stream limit. Unknown pages stay on the conservative CDP path.
/// </summary>
internal static class TikTokFileUploadTransportRegistry
{
    private sealed class Entry(TikTokFileUploadTransport transport)
    {
        public TikTokFileUploadTransport Transport { get; } = transport;
    }

    private static readonly ConditionalWeakTable<IPage, Entry> Entries = new();
    private static readonly object Sync = new();

    public static void Mark(IPage page, TikTokFileUploadTransport transport)
    {
        lock (Sync)
        {
            Entries.Remove(page);
            Entries.Add(page, new Entry(transport));
        }
    }

    public static TikTokFileUploadTransport Resolve(IPage page) =>
        Entries.TryGetValue(page, out var entry)
            ? entry.Transport
            : TikTokFileUploadTransport.CdpPathInjection;
}
