using System.Windows;
using System.Windows.Media;

namespace Desktop_Frames
{
    /// <summary>
    /// Applies a slim, arrow-less overlay scrollbar (thumb tinted from a frame colour) to any element's
    /// scope, so a control's scrollbars blend with the frame instead of using the stark default chrome.
    /// Used by the icon/normal view ScrollViewer; the Details view themes its scrollbars inline.
    /// </summary>
    public static class ThemedScrollBar
    {
        private static string Hex(Color c, byte a) => $"#{a:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>Merges an implicit ScrollBar style (thumb derived from <paramref name="c"/>) into scope.Resources.</summary>
        public static void Apply(FrameworkElement scope, Color c)
        {
            if (scope == null) return;
            try
            {
                string xaml = Xaml.Replace("%THUMB%", Hex(c, 0x66));
                var dict = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
                scope.Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }

        private const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ScrollBar'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Width' Value='10'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Grid Background='Transparent'>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.DecreaseRepeatButton>
                <RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False' Opacity='0' IsTabStop='False'/>
              </Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False' Opacity='0' IsTabStop='False'/>
              </Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb MinHeight='24'>
                  <Thumb.Template>
                    <ControlTemplate TargetType='Thumb'>
                      <Border CornerRadius='4' Background='%THUMB%' Margin='2,0,2,0'/>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/>
        <Setter Property='Height' Value='10'/>
        <Setter Property='Template'>
          <Setter.Value>
            <ControlTemplate TargetType='ScrollBar'>
              <Grid Background='Transparent'>
                <Track x:Name='PART_Track'>
                  <Track.DecreaseRepeatButton>
                    <RepeatButton Command='ScrollBar.PageLeftCommand' Focusable='False' Opacity='0' IsTabStop='False'/>
                  </Track.DecreaseRepeatButton>
                  <Track.IncreaseRepeatButton>
                    <RepeatButton Command='ScrollBar.PageRightCommand' Focusable='False' Opacity='0' IsTabStop='False'/>
                  </Track.IncreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb MinWidth='24'>
                      <Thumb.Template>
                        <ControlTemplate TargetType='Thumb'>
                          <Border CornerRadius='4' Background='%THUMB%' Margin='0,2,0,2'/>
                        </ControlTemplate>
                      </Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                </Track>
              </Grid>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>
</ResourceDictionary>";
    }
}
