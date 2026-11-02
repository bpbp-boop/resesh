using Resesh.App.Interop;

namespace Resesh.AppLogic.Tests;

public sealed class DriveLetterReservationsTests
{
    [Fact]
    public void TwoMountsStartingTogetherGetDifferentLetters()
    {
        var reservations = new DriveLetterReservations();
        char[] drives = ['C', 'd'];

        Assert.Equal('Z', reservations.Reserve(drives));
        Assert.Equal('Y', reservations.Reserve(drives));
        Assert.True(reservations.IsReserved('z'));
    }

    [Fact]
    public void ReleasedLetterIsHandedOutAgain()
    {
        var reservations = new DriveLetterReservations();
        var first = reservations.Reserve([]);
        Assert.Equal('Z', first);

        reservations.Release('z');

        Assert.False(reservations.IsReserved('Z'));
        Assert.Equal('Z', reservations.Reserve([]));
    }

    [Fact]
    public void SkipsLettersInUseAndRunsOutAtD()
    {
        var reservations = new DriveLetterReservations();
        var inUse = Enumerable.Range('E', 'Z' - 'E' + 1).Select(letter => (char)letter).ToList();

        Assert.Equal('D', reservations.Reserve(inUse));
        Assert.Null(reservations.Reserve(inUse));
    }

    [Fact]
    public void ConcurrentReservationsNeverShareALetter()
    {
        var reservations = new DriveLetterReservations();
        var letters = new System.Collections.Concurrent.ConcurrentBag<char?>();

        Parallel.For(0, 23, _ => letters.Add(reservations.Reserve(['C'])));

        Assert.Equal(23, letters.Count(letter => letter is not null));
        Assert.Equal(23, letters.Distinct().Count());
    }
}
