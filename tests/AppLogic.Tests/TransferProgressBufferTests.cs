using Resesh.App.Controls;

namespace Resesh.AppLogic.Tests;

public sealed class TransferProgressBufferTests
{
    [Fact]
    public async Task Large_transfer_with_stalled_ui_keeps_only_latest_progress()
    {
        var buffer = new TransferProgressBuffer();
        buffer.Start();
        const long total = 1_234_803_097; // About 1.15 GiB, including a partial final chunk.
        await Task.Run(() =>
        {
            for (long done = 0; done < total; done += 64 * 1024)
                buffer.Report(new("Downloading", "large.bin", 1, 1, done, total));
            buffer.Report(new("Downloading", "large.bin", 1, 1, total, total));
        });

        Assert.Equal(total, buffer.TakeLatest()!.Value.Done);
        Assert.Null(buffer.TakeLatest());
    }

    [Fact]
    public void Ending_operation_discards_pending_and_late_progress()
    {
        var buffer = new TransferProgressBuffer();
        buffer.Start();
        buffer.Report(new("Downloading", "old.bin", 1, 1, 100, 100));
        buffer.Stop(); // Completion, cancellation, failure, or closing the pane.
        buffer.Report(new("Downloading", "old.bin", 1, 1, 100, 100));
        Assert.Null(buffer.TakeLatest());

        buffer.Start();
        Assert.Null(buffer.TakeLatest());
        var next = new TransferProgress("Uploading", "next.bin", 2, 3, 0, 200);
        buffer.Report(next);
        Assert.Equal(next, buffer.TakeLatest());
    }

    [Fact]
    public void Sampling_across_files_preserves_latest_file_metadata()
    {
        var buffer = new TransferProgressBuffer();
        buffer.Start();
        buffer.Report(new("Downloading", "first.bin", 1, 2, 100, 100));
        var next = new TransferProgress("Downloading", "empty.bin", 2, 2, 0, 0);
        buffer.Report(next);
        Assert.Equal(next, buffer.TakeLatest());
        Assert.Null(buffer.TakeLatest());
    }
}
