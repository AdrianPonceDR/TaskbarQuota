using TaskbarQuota.Interop;
using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

public class ClassicTaskbarSpaceReservationTests
{
    private const int WidgetWidth = 410;

    [Fact]
    public void TryApplyRight_AfterDispose_IsAlwaysANoOp()
    {
        using var reservation = new ClassicTaskbarSpaceReservation(IntPtr.Zero);
        reservation.Dispose();

        bool applied = reservation.TryApplyRight(
            R(0, 0, 1920, 48),
            R(1600, 0, 1920, 48),
            widgetWidth: 200,
            clearance: 6,
            out _);

        Assert.False(applied);
    }

    [Theory]
    [InlineData(172, 3157)]
    [InlineData(220, 3109)]
    [InlineData(280, 3049)]
    public void TryComputeRightPlacement_WidgetModesUseTheirActualWidth(
        int widgetWidth,
        int expectedOffset)
    {
        bool found = ClassicTaskbarSpaceReservation.TryComputeRightPlacement(
            R(0, 0, 3840, 90),
            R(3335, 0, 3840, 90),
            widgetWidth,
            clearance: 6,
            out int offset,
            out RECT widgetRect);

        Assert.True(found);
        Assert.Equal(expectedOffset, offset);
        AssertRect(widgetRect, expectedOffset, 0, 3329, 90);
    }

    [Fact]
    public void TryComputeRightPlacement_NegativeTaskbarOrigin_ReturnsClientOffset()
    {
        bool found = ClassicTaskbarSpaceReservation.TryComputeRightPlacement(
            R(-1920, 1000, 0, 1080),
            R(-400, 1000, 0, 1080),
            widgetWidth: 300,
            clearance: 6,
            out int offset,
            out RECT widgetRect);

        Assert.True(found);
        Assert.Equal(1214, offset);
        AssertRect(widgetRect, -706, 1000, -406, 1080);
    }

    [Fact]
    public void TryComputeRightPlacement_NegativeClearance_IsClampedToZero()
    {
        bool found = ClassicTaskbarSpaceReservation.TryComputeRightPlacement(
            R(0, 0, 1920, 48),
            R(1600, 0, 1920, 48),
            widgetWidth: 200,
            clearance: -10,
            out int offset,
            out RECT widgetRect);

        Assert.True(found);
        Assert.Equal(1400, offset);
        AssertRect(widgetRect, 1400, 0, 1600, 48);
    }

    [Fact]
    public void TryComputeRightPlacement_WidgetDoesNotFitBeforeTray_ReturnsFalse()
    {
        bool found = ClassicTaskbarSpaceReservation.TryComputeRightPlacement(
            R(0, 0, 500, 48),
            R(100, 0, 500, 48),
            widgetWidth: 200,
            clearance: 6,
            out _,
            out _);

        Assert.False(found);
    }

    [Fact]
    public void TryComputeRightPlacement_NoVerticalIntersection_ReturnsFalse()
    {
        bool found = ClassicTaskbarSpaceReservation.TryComputeRightPlacement(
            R(0, 0, 1920, 48),
            R(1600, 48, 1920, 96),
            widgetWidth: 200,
            clearance: 6,
            out _,
            out _);

        Assert.False(found);
    }

    [Fact]
    public void TryComputeRightPlacement_NotificationAreaOutsideTaskbar_ReturnsFalse()
    {
        bool found = ClassicTaskbarSpaceReservation.TryComputeRightPlacement(
            R(0, 0, 1920, 48),
            R(1920, 0, 2200, 48),
            widgetWidth: 200,
            clearance: 6,
            out _,
            out _);

        Assert.False(found);
    }

    [Fact]
    public void TryComputeReservedTaskSwitcherRect_OverlappingRightEdge_ShortensOnlyRightEdge()
    {
        bool found = ClassicTaskbarSpaceReservation.TryComputeReservedTaskSwitcherRect(
            R(110, 0, 3335, 90),
            R(2907, 0, 3321, 90),
            out RECT result);

        Assert.True(found);
        AssertRect(result, 110, 0, 2907, 90);
    }

    [Fact]
    public void TryComputeReservedTaskSwitcherRect_AlreadyBeforeWidget_RemainsUnchanged()
    {
        bool found = ClassicTaskbarSpaceReservation.TryComputeReservedTaskSwitcherRect(
            R(110, 0, 2800, 90),
            R(2907, 0, 3321, 90),
            out RECT result);

        Assert.True(found);
        AssertRect(result, 110, 0, 2800, 90);
    }

    [Fact]
    public void TryComputeReservedTaskSwitcherRect_WidgetConsumesWholeSwitcher_ReturnsFalse()
    {
        RECT switcher = R(110, 0, 3335, 90);

        bool found = ClassicTaskbarSpaceReservation.TryComputeReservedTaskSwitcherRect(
            switcher,
            R(100, 0, 500, 90),
            out RECT result);

        Assert.False(found);
        AssertRect(result, switcher.left, switcher.top, switcher.right, switcher.bottom);
    }

    [Fact]
    public void TryComputeReservedTaskSwitcherRect_NoVerticalIntersection_ReturnsFalse()
    {
        RECT switcher = R(110, 0, 3335, 90);

        bool found = ClassicTaskbarSpaceReservation.TryComputeReservedTaskSwitcherRect(
            switcher,
            R(2907, 90, 3321, 180),
            out RECT result);

        Assert.False(found);
        AssertRect(result, switcher.left, switcher.top, switcher.right, switcher.bottom);
    }

    [Fact]
    public void CustomPosition_ThatStillFits_KeepsThePreferredPosition()
    {
        var fallback = new ClassicTaskbarCustomPositionFallback();

        ClassicCustomPositionDecision decision = fallback.Resolve(
            preferredX: 1431,
            gaps: G((761, 1841), (2201, 3281)),
            widgetWidth: WidgetWidth,
            canUseRightReservation: true);

        Assert.False(decision.UseRightReservation);
        Assert.Equal(1431, decision.FittingX);
        Assert.False(fallback.IsActive);
    }

    [Fact]
    public void CustomPosition_WhenNoGapFits_RequestsTraySideFallback()
    {
        var fallback = new ClassicTaskbarCustomPositionFallback();

        ClassicCustomPositionDecision decision = fallback.Resolve(
            preferredX: 1431,
            gaps: G((0, 291), (399, 401)),
            widgetWidth: WidgetWidth,
            canUseRightReservation: true);

        Assert.True(decision.UseRightReservation);
        Assert.Null(decision.FittingX);
        Assert.True(fallback.IsActive);
    }

    [Fact]
    public void UnsupportedLayout_WithNoGap_KeepsExistingPlacementBehavior()
    {
        var fallback = new ClassicTaskbarCustomPositionFallback();

        ClassicCustomPositionDecision decision = fallback.Resolve(
            preferredX: 1431,
            gaps: G((0, 291), (399, 401)),
            widgetWidth: WidgetWidth,
            canUseRightReservation: false);

        Assert.False(decision.UseRightReservation);
        Assert.Null(decision.FittingX);
        Assert.False(fallback.IsActive);
    }

    [Fact]
    public void CustomFallback_UsesRightReservationAndPreservesPreferredPosition()
    {
        var fallback = new ClassicTaskbarCustomPositionFallback();
        ClassicCustomPositionDecision decision = fallback.Resolve(
            preferredX: 1431,
            gaps: G((0, 291), (399, 401)),
            widgetWidth: WidgetWidth,
            canUseRightReservation: true);

        bool found = ClassicTaskbarSpaceReservation.TryComputeRightPlacement(
            R(0, 0, 3840, 90),
            R(3281, 0, 3840, 90),
            WidgetWidth,
            clearance: 13,
            out int fallbackX,
            out _);

        Assert.True(found);
        Assert.Equal(2858, fallbackX);
        Assert.Equal(1431, decision.PreferredX);
    }

    [Fact]
    public void DefaultPosition_RemainsEligibleForClassicRightReservation()
    {
        Assert.True(TaskBarWidget.CanUseClassicRightReservation(
            isVisible: true,
            isPrimaryTaskbar: true,
            hasNotificationArea: true,
            isRtlUi: false));

        Assert.True(ClassicTaskbarSpaceReservation.TryComputeRightPlacement(
            R(0, 0, 3840, 90),
            R(3281, 0, 3840, 90),
            WidgetWidth,
            clearance: 13,
            out int offset,
            out _));
        Assert.Equal(2858, offset);
    }

    [Fact]
    public void DragReset_CancelReactivation_AndHideReset_AreStable()
    {
        var fallback = new ClassicTaskbarCustomPositionFallback();
        fallback.Resolve(1431, G((0, 291)), WidgetWidth, canUseRightReservation: true);
        Assert.True(fallback.IsActive);

        fallback.Reset();

        Assert.False(fallback.IsActive);
        fallback.Activate();
        Assert.True(fallback.IsActive);
        fallback.Reset();
        Assert.False(fallback.IsActive);

        ClassicCustomPositionDecision settled = fallback.Resolve(
            1431,
            G((761, 1841)),
            WidgetWidth,
            canUseRightReservation: true);
        Assert.False(settled.UseRightReservation);
        Assert.Equal(1431, settled.PreferredX);
    }

    [Fact]
    public void RepeatedUpdates_DoNotOscillateAndRecoverOnlyAfterStableGap()
    {
        var fallback = new ClassicTaskbarCustomPositionFallback();
        var crowded = G((0, 291), (399, 401));
        var recovered = G((761, 1841));

        for (int i = 0; i < 4; i++)
            Assert.True(fallback.Resolve(1431, crowded, WidgetWidth, true).UseRightReservation);

        Assert.True(fallback.Resolve(1431, recovered, WidgetWidth, true).UseRightReservation);
        Assert.False(fallback.Resolve(1431, recovered, WidgetWidth, true).UseRightReservation);
        Assert.False(fallback.Resolve(1431, recovered, WidgetWidth, true).UseRightReservation);
        Assert.False(fallback.IsActive);
    }

    [Fact]
    public void RecoveryRequiresPreferredGapToRemainAvailable()
    {
        var fallback = new ClassicTaskbarCustomPositionFallback();
        fallback.Resolve(1431, G((0, 291)), WidgetWidth, true);

        // Another wide gap exists, but the preferred x=1431 still does not fit in it completely.
        Assert.True(fallback.Resolve(1431, G((700, 1800)), WidgetWidth, true).UseRightReservation);
        Assert.True(fallback.Resolve(1431, G((900, 1900)), WidgetWidth, true).UseRightReservation);
        Assert.False(fallback.Resolve(1431, G((900, 1900)), WidgetWidth, true).UseRightReservation);
    }

    [Fact]
    public void ReservedSlot_DoesNotTriggerItsOwnRecoveryOrOscillation()
    {
        var fallback = new ClassicTaskbarCustomPositionFallback();
        fallback.Resolve(2858, G((0, 291)), WidgetWidth, true);

        for (int i = 0; i < 5; i++)
        {
            ClassicCustomPositionDecision decision = fallback.Resolve(
                preferredX: 2858,
                gaps: G((2858, 3268)),
                widgetWidth: WidgetWidth,
                canUseRightReservation: true,
                reservedFallbackX: 2858);

            Assert.True(decision.UseRightReservation);
            Assert.True(fallback.IsActive);
        }
    }

    private static List<(int start, int end)> G(params (int start, int end)[] gaps)
        => new(gaps);

    private static RECT R(int left, int top, int right, int bottom)
        => new() { left = left, top = top, right = right, bottom = bottom };

    private static void AssertRect(RECT actual, int left, int top, int right, int bottom)
    {
        Assert.Equal(left, actual.left);
        Assert.Equal(top, actual.top);
        Assert.Equal(right, actual.right);
        Assert.Equal(bottom, actual.bottom);
    }
}
