using DirectN;
using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Tests;

/// <summary>
/// The immediate-mode widgets every window is built from, driven frame by frame on an offscreen
/// target: what counts as a click, what a clip or an inert frame hides, and what a field does with
/// keys. A regression here breaks every window at once.
/// </summary>
public sealed class UiWidgetTests : IDisposable
{
    private readonly Offscreen _screen = new(400, 200);
    private readonly Ui _ui;
    private static readonly Rect Box = new(100, 50, 120, 32);
    private static readonly Point Inside = Box.Center;
    private static readonly Point Outside = new(20, 180);
    private static readonly int Id = Ui.Id("test.widget");

    public UiWidgetTests() => _ui = new Ui(_screen.Resources);

    public void Dispose() => _screen.Dispose();

    private bool Button() => _ui.Button(Id, Box, "Go", ButtonStyle.Primary);

    /// <summary>Press at one point and release at another, reporting the release frame.</summary>
    private bool PressAndRelease(Point press, Point release, Func<bool> widget)
    {
        _screen.Frame(_ui, press, down: false, () => widget());
        _screen.Frame(_ui, press, down: true, () => widget());
        var clicked = false;
        _screen.Frame(_ui, release, down: false, () => clicked = widget());
        return clicked;
    }

    [Fact]
    public void AButtonClicksOnAReleaseOverTheSameButtonItWasPressedOn() =>
        Assert.True(PressAndRelease(Inside, Inside, Button));

    [Fact]
    public void DraggingOffAButtonBeforeReleasingCancelsTheClick() =>
        Assert.False(PressAndRelease(Inside, Outside, Button));

    [Fact]
    public void AReleaseOverAButtonThatWasNotPressedIsNotAClick() =>
        Assert.False(PressAndRelease(Outside, Inside, Button));

    [Fact]
    public void ADisabledButtonNeverClicks() =>
        Assert.False(_screen.Click(_ui, Inside, () => _ui.Button(Id, Box, "Go", enabled: false)));

    [Fact]
    public void AWidgetClippedOutOfViewCannotBeClicked()
    {
        var clicked = _screen.Click(_ui, Inside, () =>
        {
            _ui.PushClip(new Rect(0, 0, 50, 50));
            var result = Button();
            _ui.PopClip();
            return result;
        });
        Assert.False(clicked);
    }

    [Fact]
    public void AnInertFrameIgnoresThePointer()
    {
        _ui.Inert = true;
        Assert.False(_screen.Click(_ui, Inside, Button));
        Assert.False(_ui.WantsPointer);
    }

    [Fact]
    public void HoveringAButtonClaimsThePointerFromTheCanvas()
    {
        _screen.Frame(_ui, Inside, down: false, () => Button());
        Assert.True(_ui.WantsPointer);

        _screen.Frame(_ui, Outside, down: false, () => Button());
        Assert.False(_ui.WantsPointer);
    }

    [Fact]
    public void APrimaryButtonIsDrawnInTheAccentAndNowhereElse()
    {
        _screen.Frame(_ui, Outside, down: false, () => Button());

        Assert.Equal(_ui.Theme.Accent, _screen.Pixel((int)Box.X + 6, (int)Box.Center.Y));
        Assert.Equal(0, _screen.CountOther(new Rect(0, 0, 90, 200), _ui.Theme.SurfaceWindow));
    }

    [Fact]
    public void AnUnbalancedClipIsUnwoundAtTheEndOfTheFrame()
    {
        // EndDraw faults with D2DERR_WRONG_STATE when a clip is left pushed.
        _screen.Frame(_ui, Outside, down: false, () => _ui.PushClip(new Rect(0, 0, 10, 10)));
        _screen.Frame(_ui, Outside, down: false, () => _ui.FillRect(new Rect(300, 100, 20, 20), Rgba.Black));

        Assert.Equal(Rgba.Black, _screen.Pixel(310, 110));
    }

    [Fact]
    public void AToggleReportsItsClick() =>
        Assert.True(_screen.Click(_ui, new Point(Box.Right - 10, Box.Center.Y), () => _ui.Toggle(Id, Box, on: false)));

    [Fact]
    public void ASliderJumpsToAPressAndFollowsTheDragPastItsEnds()
    {
        var value = 0.0;
        bool Slide() => _ui.Slider(Id, Box, 0, 100, ref value);

        _screen.Frame(_ui, new Point(160, Box.Center.Y), down: false, () => Slide());
        _screen.Frame(_ui, new Point(160, Box.Center.Y), down: true, () => Slide());
        Assert.Equal(50, value, 6);

        // Still held, dragged well past the right end: clamped, not lost.
        _screen.Frame(_ui, new Point(390, 190), down: true, () => Slide());
        Assert.Equal(100, value, 6);
    }

    [Fact]
    public void ASegmentedControlReportsOnlyANewChoice()
    {
        string[] options = ["One", "Two", "Three"];
        var width = _ui.SegmentedWidth(options);
        var bounds = new Rect(10, 10, width, 32);
        int? Segmented() => _ui.Segmented(Id, bounds, options, selected: 0);

        Assert.Null(_screen.Click(_ui, new Point(bounds.X + 12, bounds.Center.Y), Segmented));
        Assert.Equal(2, _screen.Click(_ui, new Point(bounds.Right - 12, bounds.Center.Y), Segmented));
    }

    [Fact]
    public void AStepperStepsDownOnItsLeftAndUpOnItsRight()
    {
        Assert.Equal(-1, _screen.Click(_ui, new Point(Box.X + 10, Box.Center.Y), () => _ui.Stepper(Id, Box, "5")));
        Assert.Equal(1, _screen.Click(_ui, new Point(Box.Right - 10, Box.Center.Y), () => _ui.Stepper(Id, Box, "5")));
    }

    private FieldResult Field(string value = "12") => _ui.Field(Id, Box, value, char.IsAsciiDigit, maxLength: 3);

    [Fact]
    public void TypingIntoAFocusedFieldReplacesItsValueThenAppends()
    {
        _screen.Click(_ui, Inside, () => Field());
        Assert.True(_ui.HasKeyboardFocus);

        _ui.Char('4');
        _ui.Char('x');      // refused by the filter
        _ui.Char('2');
        var result = default(FieldResult);
        _screen.Frame(_ui, Inside, down: false, () => result = Field());

        Assert.True(result.Changed);
        Assert.Equal("42", result.Text);
    }

    [Fact]
    public void AFieldStopsAtItsMaximumLength()
    {
        _screen.Click(_ui, Inside, () => Field());
        foreach (var digit in "98765") _ui.Char(digit);
        var result = default(FieldResult);
        _screen.Frame(_ui, Inside, down: false, () => result = Field());

        Assert.Equal("987", result.Text);
    }

    [Fact]
    public void BackspaceOnAFreshlyFocusedFieldClearsIt()
    {
        _screen.Click(_ui, Inside, () => Field());
        _ui.Key(VIRTUAL_KEY.VK_BACK, shift: false, control: false);
        var result = default(FieldResult);
        _screen.Frame(_ui, Inside, down: false, () => result = Field());

        Assert.Equal("", result.Text);
    }

    [Fact]
    public void CtrlAThenBackspaceClearsTextTypedSinceFocusing()
    {
        _screen.Click(_ui, Inside, () => Field());
        foreach (var digit in "345") _ui.Char(digit);
        _screen.Frame(_ui, Inside, down: false, () => Field("345"));

        _ui.Key(VIRTUAL_KEY.VK_A, shift: false, control: true);
        _ui.Key(VIRTUAL_KEY.VK_BACK, shift: false, control: false);
        var result = default(FieldResult);
        _screen.Frame(_ui, Inside, down: false, () => result = Field("345"));

        Assert.Equal("", result.Text);
    }

    [Fact]
    public void TypingAfterCtrlAReplacesEverything()
    {
        _screen.Click(_ui, Inside, () => Field());
        _ui.Char('9');
        _screen.Frame(_ui, Inside, down: false, () => Field("129"));

        _ui.Key(VIRTUAL_KEY.VK_A, shift: false, control: true);
        _ui.Char('7');
        var result = default(FieldResult);
        _screen.Frame(_ui, Inside, down: false, () => result = Field("129"));

        Assert.Equal("7", result.Text);
    }

    [Theory]
    [InlineData(VIRTUAL_KEY.VK_UP, false, 1)]
    [InlineData(VIRTUAL_KEY.VK_UP, true, 10)]
    [InlineData(VIRTUAL_KEY.VK_DOWN, false, -1)]
    [InlineData(VIRTUAL_KEY.VK_DOWN, true, -10)]
    public void ArrowKeysStepANumericField(VIRTUAL_KEY key, bool shift, int step)
    {
        _screen.Click(_ui, Inside, () => Field());
        _ui.Key(key, shift, control: false);
        var result = default(FieldResult);
        _screen.Frame(_ui, Inside, down: false, () => result = Field());

        Assert.Equal(step, result.Step);
    }

    [Fact]
    public void EnterAndAClickElsewhereBothLetGoOfTheKeyboard()
    {
        _screen.Click(_ui, Inside, () => Field());
        _ui.Key(VIRTUAL_KEY.VK_RETURN, shift: false, control: false);
        _screen.Frame(_ui, Inside, down: false, () => Field());
        Assert.False(_ui.HasKeyboardFocus);

        _screen.Click(_ui, Inside, () => Field());
        Assert.True(_ui.HasKeyboardFocus);
        _screen.Frame(_ui, Outside, down: true, () => Field());
        Assert.False(_ui.HasKeyboardFocus);
    }

    [Fact]
    public void AFieldAsksForTheTextCursorAndNothingElseDoesOutsideIt()
    {
        _screen.Frame(_ui, Outside, down: false, () => Field());

        Assert.Equal(PointerCursor.Text, _ui.CursorAt(Inside));
        Assert.Equal(PointerCursor.Arrow, _ui.CursorAt(Outside));
    }

    [Fact]
    public void ClickableControlsAskForTheHandButADisabledOneDoesNot()
    {
        var toggle = new Rect(100, 120, 120, 32);
        _screen.Frame(_ui, Outside, down: false, () =>
        {
            _ui.Button(Id, Box, "Go", enabled: false);
            _ui.Toggle(Ui.Id("test.toggle"), toggle, on: true);
        });

        Assert.Equal(PointerCursor.Arrow, _ui.CursorAt(Inside));
        Assert.Equal(PointerCursor.Hand, _ui.CursorAt(new Point(toggle.Right - 10, toggle.Center.Y)));
    }

    [Fact]
    public void AControlDrawnOverAnotherCanKeepTheArrow()
    {
        // The caption buttons: clickable, but with the standard cursor over whatever lies beneath.
        _screen.Frame(_ui, Outside, down: false, () =>
        {
            Button();
            _ui.Interact(Ui.Id("test.caption"), Box, PointerCursor.Arrow);
        });

        Assert.Equal(PointerCursor.Arrow, _ui.CursorAt(Inside));
    }

    [Fact]
    public void AControlScrolledOutOfItsClipAsksForNoCursorThere()
    {
        _screen.Frame(_ui, Outside, down: false, () =>
        {
            _ui.PushClip(new Rect(0, 0, 150, 200));
            Button();
            _ui.PopClip();
        });

        Assert.Equal(PointerCursor.Hand, _ui.CursorAt(new Point(120, Box.Center.Y)));
        Assert.Equal(PointerCursor.Arrow, _ui.CursorAt(new Point(200, Box.Center.Y)));
    }

    [Fact]
    public void AToggleKnobSitsRightWhenOnAndLeftWhenOff()
    {
        var off = Ui.Id("test.toggle.off");
        _screen.Frame(_ui, Outside, down: false, () => _ui.Toggle(off, Box, on: false));
        var offRight = _screen.Pixel((int)Box.Right - 10, (int)Box.Center.Y);

        var on = Ui.Id("test.toggle.on");
        _screen.Frame(_ui, Outside, down: false, () => _ui.Toggle(on, Box, on: true));

        Assert.Equal(_ui.Theme.AccentText, _screen.Pixel((int)Box.Right - 10, (int)Box.Center.Y));
        Assert.NotEqual(_ui.Theme.TextSecondary, offRight);
    }

    [Theory]
    [MemberData(nameof(SettingsAndThemeTests.Themes), MemberType = typeof(SettingsAndThemeTests))]
    public void AToggleKnobStandsOutFromItsTrackInEveryThemeAndAccent(bool dark, string accent)
    {
        _ui.Theme = Theme.Resolve(dark, Accent.Named(accent));

        foreach (var on in new[] { true, false })
        {
            _screen.Frame(_ui, Outside, down: false, () => _ui.Toggle(Ui.Id($"test.toggle.{on}"), Box, on));
            var (knob, track) = on
                ? (Box.Right - 10, Box.Right - 28)
                : (Box.Right - 26, Box.Right - 8);
            var contrast = SettingsAndThemeTests.Contrast(
                _screen.Pixel((int)knob, (int)Box.Center.Y), _screen.Pixel((int)track, (int)Box.Center.Y));
            Assert.True(contrast >= 3, $"{(on ? "on" : "off")} knob contrast {contrast:0.0} in {(dark ? "dark" : "light")} {accent}");
        }
    }

    [Fact]
    public void AFieldUnderAModalSheetGetsNoTextCursor()
    {
        _screen.Frame(_ui, Outside, down: false, () =>
        {
            _ui.Inert = true;
            Field();
            _ui.Inert = false;
        });

        Assert.Equal(PointerCursor.Arrow, _ui.CursorAt(Inside));
    }

    [Fact]
    public void AnUnfocusedFieldIgnoresTypedKeys()
    {
        _ui.Char('9');
        var result = default(FieldResult);
        _screen.Frame(_ui, Outside, down: false, () => result = Field());

        Assert.False(result.Changed);
        Assert.Equal("12", result.Text);
    }

    [Fact]
    public void AnAnimationLandsAtOnceThenEasesTowardANewTarget()
    {
        var animation = Ui.Id("test.motion");
        var first = 0.0;
        var second = 0.0;
        _screen.Frame(_ui, Outside, down: false, () => first = _ui.Animate(animation, 10));
        _screen.Frame(_ui, Outside, down: false, () => second = _ui.Animate(animation, 20, duration: 10_000));

        Assert.Equal(10, first);
        Assert.InRange(second, 10, 19.9);
        Assert.True(_ui.Animating);
    }
}
