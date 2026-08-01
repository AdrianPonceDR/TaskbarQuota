using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Interop;

namespace TaskbarQuota.Taskbar
{
    /// <summary>
    /// Reserves a tray-side slot by shortening a classic task switcher from its right edge.
    /// Native Windows 11 taskbars do not expose the complete classic hierarchy and remain untouched.
    /// </summary>
    internal sealed class ClassicTaskbarSpaceReservation : IDisposable
    {
        private const string ReBarClassName = "ReBarWindow32";
        private const string TaskSwitcherClassName = "MSTaskSwWClass";
        private const string TaskListClassName = "MSTaskListWClass";
        private static readonly SetWindowPosFlags PositionFlags =
            SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE;

        private readonly object syncRoot = new();
        private readonly IntPtr hwndTaskbar;
        private readonly ReservationState taskSwitcherState = new();
        private IntPtr hwndReBar;
        private IntPtr hwndTaskSwitcher;
        private IntPtr hwndTaskList;
        private bool disposed;

        public ClassicTaskbarSpaceReservation(IntPtr hwndTaskbar)
        {
            this.hwndTaskbar = hwndTaskbar;
        }

        public bool TryApplyRight(
            RECT taskbarScreenRect,
            RECT notificationScreenRect,
            int widgetWidth,
            int clearance,
            out int widgetOffsetX)
        {
            lock (syncRoot)
            {
                widgetOffsetX = 0;
                if (disposed
                    || !TryComputeRightPlacement(
                        taskbarScreenRect,
                        notificationScreenRect,
                        widgetWidth,
                        clearance,
                        out widgetOffsetX,
                        out RECT widgetScreenRect)
                    || !TryGetTargets(out RECT rebarScreenRect, out RECT taskSwitcherScreenRect))
                {
                    return false;
                }

                RECT baseline = ObserveBaseline(taskSwitcherScreenRect, rebarScreenRect);
                if (!TryComputeReservedTaskSwitcherRect(baseline, widgetScreenRect, out RECT target))
                {
                    RestoreCore();
                    return false;
                }
                if (!TrySetWindowRect(target, rebarScreenRect))
                    return false;

                taskSwitcherState.LastAppliedScreenRect = RectsEqual(target, baseline)
                    ? null
                    : target;
                return true;
            }
        }

        public void Restore()
        {
            lock (syncRoot)
                RestoreCore();
        }

        private void RestoreCore()
        {
            if (disposed || !HasValidCachedTargets())
            {
                if (!disposed)
                    ResetTargets();
                return;
            }

            if (taskSwitcherState.BaselineClientRect is not { } baselineClientRect
                || taskSwitcherState.LastAppliedScreenRect is not { } appliedScreenRect
                || !User32.GetWindowRect(hwndReBar, out RECT rebarScreenRect)
                || !User32.GetWindowRect(hwndTaskSwitcher, out RECT currentScreenRect))
            {
                taskSwitcherState.Reset();
                return;
            }

            if (RectsEqual(currentScreenRect, appliedScreenRect))
                TrySetWindowRect(ToScreenRect(baselineClientRect, rebarScreenRect), rebarScreenRect);

            taskSwitcherState.Reset();
        }

        internal static bool TryComputeRightPlacement(
            RECT taskbarScreenRect,
            RECT notificationScreenRect,
            int widgetWidth,
            int clearance,
            out int widgetOffsetX,
            out RECT widgetScreenRect)
        {
            widgetOffsetX = 0;
            widgetScreenRect = default;
            if (!IsValid(taskbarScreenRect)
                || !IsValid(notificationScreenRect)
                || widgetWidth <= 0
                || notificationScreenRect.left <= taskbarScreenRect.left
                || notificationScreenRect.left >= taskbarScreenRect.right
                || notificationScreenRect.bottom <= taskbarScreenRect.top
                || notificationScreenRect.top >= taskbarScreenRect.bottom)
            {
                return false;
            }

            int safeClearance = Math.Max(0, clearance);
            long widgetLeft = (long)notificationScreenRect.left - safeClearance - widgetWidth;
            long offset = widgetLeft - taskbarScreenRect.left;
            if (widgetLeft < taskbarScreenRect.left
                || widgetLeft >= notificationScreenRect.left
                || offset < int.MinValue
                || offset > int.MaxValue)
            {
                return false;
            }

            widgetOffsetX = (int)offset;
            widgetScreenRect = new RECT
            {
                left = (int)widgetLeft,
                top = taskbarScreenRect.top,
                right = notificationScreenRect.left - safeClearance,
                bottom = taskbarScreenRect.bottom,
            };
            return IsValid(widgetScreenRect);
        }

        internal static bool TryComputeReservedTaskSwitcherRect(
            RECT taskSwitcherScreenRect,
            RECT widgetScreenRect,
            out RECT result)
        {
            result = taskSwitcherScreenRect;
            if (!IsValid(taskSwitcherScreenRect)
                || !IsValid(widgetScreenRect)
                || widgetScreenRect.bottom <= taskSwitcherScreenRect.top
                || widgetScreenRect.top >= taskSwitcherScreenRect.bottom)
            {
                return false;
            }

            int targetRight = Math.Min(taskSwitcherScreenRect.right, widgetScreenRect.left);
            if (targetRight <= taskSwitcherScreenRect.left)
                return false;

            result.right = targetRight;
            return true;
        }

        private bool TryGetTargets(
            out RECT rebarScreenRect,
            out RECT taskSwitcherScreenRect)
        {
            rebarScreenRect = default;
            taskSwitcherScreenRect = default;

            if (!HasValidCachedTargets())
            {
                ResetTargets();
                hwndReBar = User32.FindWindowEx(hwndTaskbar, IntPtr.Zero, ReBarClassName, null);
                hwndTaskSwitcher = hwndReBar == IntPtr.Zero
                    ? IntPtr.Zero
                    : User32.FindWindowEx(hwndReBar, IntPtr.Zero, TaskSwitcherClassName, null);
                hwndTaskList = hwndTaskSwitcher == IntPtr.Zero
                    ? IntPtr.Zero
                    : User32.FindWindowEx(hwndTaskSwitcher, IntPtr.Zero, TaskListClassName, null);
                if (!HasValidCachedTargets())
                {
                    ResetTargets();
                    return false;
                }
            }

            return User32.GetWindowRect(hwndReBar, out rebarScreenRect)
                && User32.GetWindowRect(hwndTaskSwitcher, out taskSwitcherScreenRect)
                && IsValid(rebarScreenRect)
                && IsValid(taskSwitcherScreenRect);
        }

        private bool HasValidCachedTargets()
            => hwndReBar != IntPtr.Zero
                && hwndTaskSwitcher != IntPtr.Zero
                && hwndTaskList != IntPtr.Zero
                && User32.IsWindow(hwndReBar)
                && User32.IsWindow(hwndTaskSwitcher)
                && User32.IsWindow(hwndTaskList)
                && User32.GetAncestor(hwndReBar, GetAncestorFlags.GA_PARENT) == hwndTaskbar
                && User32.GetAncestor(hwndTaskSwitcher, GetAncestorFlags.GA_PARENT) == hwndReBar
                && User32.GetAncestor(hwndTaskList, GetAncestorFlags.GA_PARENT) == hwndTaskSwitcher;

        private RECT ObserveBaseline(RECT currentScreenRect, RECT parentScreenRect)
        {
            if (taskSwitcherState.LastAppliedScreenRect is not { } applied
                || !RectsEqual(currentScreenRect, applied))
            {
                taskSwitcherState.BaselineClientRect = ToClientRect(currentScreenRect, parentScreenRect);
                taskSwitcherState.LastAppliedScreenRect = null;
            }

            taskSwitcherState.BaselineClientRect ??= ToClientRect(currentScreenRect, parentScreenRect);
            return ToScreenRect(taskSwitcherState.BaselineClientRect.Value, parentScreenRect);
        }

        private bool TrySetWindowRect(RECT targetScreenRect, RECT parentScreenRect)
        {
            if (!IsValid(targetScreenRect))
                return false;

            if (User32.GetWindowRect(hwndTaskSwitcher, out RECT currentScreenRect)
                && RectsEqual(currentScreenRect, targetScreenRect))
            {
                taskSwitcherState.FailureLogged = false;
                return true;
            }

            RECT targetClientRect = ToClientRect(targetScreenRect, parentScreenRect);
            bool success = User32.SetWindowPos(
                hwndTaskSwitcher,
                IntPtr.Zero,
                targetClientRect.left,
                targetClientRect.top,
                targetClientRect.right - targetClientRect.left,
                targetClientRect.bottom - targetClientRect.top,
                PositionFlags);
            if (success)
            {
                taskSwitcherState.FailureLogged = false;
                return true;
            }

            if (!taskSwitcherState.FailureLogged)
            {
                taskSwitcherState.FailureLogged = true;
                Log.Warning(
                    new Win32Exception(Marshal.GetLastWin32Error()),
                    "Could not reserve the classic task switcher area");
            }
            return false;
        }

        private void ResetTargets()
        {
            hwndReBar = IntPtr.Zero;
            hwndTaskSwitcher = IntPtr.Zero;
            hwndTaskList = IntPtr.Zero;
            taskSwitcherState.Reset();
        }

        private static RECT ToClientRect(RECT screenRect, RECT parentScreenRect)
            => new()
            {
                left = screenRect.left - parentScreenRect.left,
                top = screenRect.top - parentScreenRect.top,
                right = screenRect.right - parentScreenRect.left,
                bottom = screenRect.bottom - parentScreenRect.top,
            };

        private static RECT ToScreenRect(RECT clientRect, RECT parentScreenRect)
            => new()
            {
                left = clientRect.left + parentScreenRect.left,
                top = clientRect.top + parentScreenRect.top,
                right = clientRect.right + parentScreenRect.left,
                bottom = clientRect.bottom + parentScreenRect.top,
            };

        private static bool IsValid(RECT rect)
            => rect.right > rect.left && rect.bottom > rect.top;

        private static bool RectsEqual(RECT left, RECT right)
            => left.left == right.left
                && left.top == right.top
                && left.right == right.right
                && left.bottom == right.bottom;

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed)
                    return;
                RestoreCore();
                disposed = true;
                ResetTargets();
            }
        }

        private sealed class ReservationState
        {
            public RECT? BaselineClientRect { get; set; }
            public RECT? LastAppliedScreenRect { get; set; }
            public bool FailureLogged { get; set; }

            public void Reset()
            {
                BaselineClientRect = null;
                LastAppliedScreenRect = null;
                FailureLogged = false;
            }
        }
    }

    /// <summary>
    /// Keeps a custom-position fallback reserved until the preferred X has fitted completely twice in a row.
    /// Placement still comes from <see cref="TaskBarWidget.PlaceInFittingGap"/>; this state only prevents
    /// a transient taskbar layout from releasing and immediately recreating the classic reservation.
    /// </summary>
    internal sealed class ClassicTaskbarCustomPositionFallback
    {
        private const int StableGapObservationsRequired = 2;

        private readonly object syncRoot = new();
        private int? recoveryCandidateX;
        private int recoveryCandidateObservations;
        private bool isActive;

        public bool IsActive
        {
            get
            {
                lock (syncRoot)
                    return isActive;
            }
        }

        public ClassicCustomPositionDecision Resolve(
            int preferredX,
            List<(int start, int end)> gaps,
            int widgetWidth,
            bool canUseRightReservation,
            int? reservedFallbackX = null)
        {
            lock (syncRoot)
            {
                return ResolveCore(
                    preferredX,
                    gaps,
                    widgetWidth,
                    canUseRightReservation,
                    reservedFallbackX);
            }
        }

        private ClassicCustomPositionDecision ResolveCore(
            int preferredX,
            List<(int start, int end)> gaps,
            int widgetWidth,
            bool canUseRightReservation,
            int? reservedFallbackX)
        {
            int? fittingX = TaskBarWidget.PlaceInFittingGap(preferredX, gaps, widgetWidth);
            if (!canUseRightReservation)
            {
                ResetCore();
                return new ClassicCustomPositionDecision(preferredX, fittingX, false);
            }

            if (fittingX is not { } candidateX)
            {
                isActive = true;
                ResetRecoveryCandidate();
                return new ClassicCustomPositionDecision(preferredX, null, true);
            }

            if (!isActive)
            {
                ResetRecoveryCandidate();
                return new ClassicCustomPositionDecision(preferredX, candidateX, false);
            }

            // The active reservation creates its own tray-side gap. That gap must not count as evidence that
            // the saved custom position is safe again, or every poll would alternate between custom and
            // fallback. Recover only when the preferred X itself fits completely in an observed gap.
            if (!FitsEntirelyInGap(preferredX, gaps, widgetWidth, reservedFallbackX))
            {
                ResetRecoveryCandidate();
                return new ClassicCustomPositionDecision(preferredX, candidateX, true);
            }

            if (recoveryCandidateX == preferredX)
            {
                recoveryCandidateObservations++;
            }
            else
            {
                recoveryCandidateX = preferredX;
                recoveryCandidateObservations = 1;
            }

            if (recoveryCandidateObservations < StableGapObservationsRequired)
                return new ClassicCustomPositionDecision(preferredX, candidateX, true);

            isActive = false;
            ResetRecoveryCandidate();
            return new ClassicCustomPositionDecision(preferredX, preferredX, false);
        }

        public void Reset()
        {
            lock (syncRoot)
                ResetCore();
        }

        public void Activate()
        {
            lock (syncRoot)
            {
                isActive = true;
                ResetRecoveryCandidate();
            }
        }

        private void ResetCore()
        {
            isActive = false;
            ResetRecoveryCandidate();
        }

        private void ResetRecoveryCandidate()
        {
            recoveryCandidateX = null;
            recoveryCandidateObservations = 0;
        }

        private static bool FitsEntirelyInGap(
            int positionX,
            List<(int start, int end)> gaps,
            int widgetWidth,
            int? reservedFallbackX)
        {
            long positionRight = (long)positionX + widgetWidth;
            if (reservedFallbackX is { } fallbackX
                && positionX < (long)fallbackX + widgetWidth
                && positionRight > fallbackX)
            {
                return false;
            }

            foreach (var (start, end) in gaps)
            {
                if (positionX >= start && positionRight <= end)
                    return true;
            }
            return false;
        }
    }

    internal readonly record struct ClassicCustomPositionDecision(
        int PreferredX,
        int? FittingX,
        bool UseRightReservation)
    {
        public int PositionToPersist(bool reservationApplied, int displayedX)
            => UseRightReservation && reservationApplied ? PreferredX : displayedX;
    }
}
