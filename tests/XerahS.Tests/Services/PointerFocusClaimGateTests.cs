using NUnit.Framework;
using XerahS.RegionCapture.Services;

namespace XerahS.Tests.Services;

[TestFixture]
public class PointerFocusClaimGateTests
{
    [Test]
    public void FirstObservedPosition_NeverClaims()
    {
        // Avalonia raises a synthetic PointerMoved on every overlay as it opens (observed on
        // COSMIC: both monitors fire within 1 ms). The first event only records the baseline.
        var gate = new PointerFocusClaimGate();

        Assert.That(gate.ObserveMove(1200, 700), Is.False);
    }

    [Test]
    public void RepeatedSyntheticPosition_DoesNotClaim()
    {
        // Re-raised synthetic events carry the same coordinates — no real motion, no claim.
        var gate = new PointerFocusClaimGate();
        gate.ObserveMove(1200, 700);

        Assert.That(gate.ObserveMove(1200, 700), Is.False);
        Assert.That(gate.ObserveMove(1200, 700), Is.False);
    }

    [Test]
    public void SubThresholdJitter_DoesNotClaim()
    {
        var gate = new PointerFocusClaimGate(movementThresholdPx: 4.0);
        gate.ObserveMove(1200, 700);

        Assert.That(gate.ObserveMove(1202, 701), Is.False);
    }

    [Test]
    public void RealMovement_ClaimsExactlyOnce()
    {
        var gate = new PointerFocusClaimGate(movementThresholdPx: 4.0);
        gate.ObserveMove(1200, 700);

        Assert.That(gate.ObserveMove(1230, 715), Is.True, "first real movement should claim");
        Assert.That(gate.ObserveMove(1300, 800), Is.False, "claim must fire only once");
    }

    [Test]
    public void MovementAccumulatesFromBaseline_NotBetweenEvents()
    {
        // Slow, small steps still add up to real motion relative to where the pointer started.
        var gate = new PointerFocusClaimGate(movementThresholdPx: 4.0);
        gate.ObserveMove(1200, 700);
        Assert.That(gate.ObserveMove(1202, 700), Is.False);
        Assert.That(gate.ObserveMove(1204, 700), Is.False);

        Assert.That(gate.ObserveMove(1206, 700), Is.True);
    }

    [Test]
    public void Press_ClaimsImmediately_EvenWithoutMovement()
    {
        // A button press is always real user input, never synthetic.
        var gate = new PointerFocusClaimGate();
        gate.ObserveMove(1200, 700);

        Assert.That(gate.ObservePress(), Is.True);
    }

    [Test]
    public void Press_BeforeAnyMove_Claims()
    {
        var gate = new PointerFocusClaimGate();

        Assert.That(gate.ObservePress(), Is.True);
    }

    [Test]
    public void Press_AfterClaim_DoesNotClaimAgain()
    {
        var gate = new PointerFocusClaimGate(movementThresholdPx: 4.0);
        gate.ObserveMove(1200, 700);
        gate.ObserveMove(1300, 800);

        Assert.That(gate.ObservePress(), Is.False);
    }

    [Test]
    public void MoveAfterPressClaim_DoesNotClaimAgain()
    {
        var gate = new PointerFocusClaimGate();
        gate.ObservePress();

        Assert.That(gate.ObserveMove(0, 0), Is.False);
        Assert.That(gate.ObserveMove(500, 500), Is.False);
    }
}
