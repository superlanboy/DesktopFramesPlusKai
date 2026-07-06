using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop_Frames
{
    /// <summary>
    /// Applies a dark style to WPF <see cref="ContextMenu"/>s when Windows is in dark mode.
    /// Shared by the Portal Details view (Sort/Group menu) and the icon-frame right-click menus
    /// so they all follow the OS light/dark setting. Classic/native menus are handled separately
    /// by <see cref="ShellContextMenu"/>.
    /// </summary>
    public static class DarkMenuTheme
    {
        private static ResourceDictionary _res;

        /// <summary>Dark-styles <paramref name="cm"/> in place, but only when the OS is in dark mode.</summary>
        public static void Apply(ContextMenu cm)
        {
            if (cm == null) return;
            try
            {
                if (!ShellContextMenu.IsSystemDark()) return;
                if (_res == null)
                    _res = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(Xaml);

                if (!cm.Resources.MergedDictionaries.Contains(_res))
                    cm.Resources.MergedDictionaries.Add(_res);
                if (_res["DarkContextMenu"] is Style s) cm.Style = s;
            }
            catch { }
        }

        /// <summary>
        /// Gives a TextBox a dark Cut/Copy/Paste context menu in dark mode. In light mode it leaves
        /// the native menu in place (it already looks right there). The default WPF TextBox editing
        /// menu is a plain light menu, so without this the Notes editor stayed light.
        /// </summary>
        public static void AttachEditMenu(System.Windows.Controls.Primitives.TextBoxBase tb)
        {
            if (tb == null) return;
            try
            {
                if (!ShellContextMenu.IsSystemDark()) return; // keep the native menu when light

                var menu = new ContextMenu();
                menu.Items.Add(new MenuItem { Header = "Cut", InputGestureText = "Ctrl+X", Command = ApplicationCommands.Cut, CommandTarget = tb });
                menu.Items.Add(new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C", Command = ApplicationCommands.Copy, CommandTarget = tb });
                menu.Items.Add(new MenuItem { Header = "Paste", InputGestureText = "Ctrl+V", Command = ApplicationCommands.Paste, CommandTarget = tb });
                Apply(menu);
                tb.ContextMenu = menu;
            }
            catch { }
        }

        // A keyed ContextMenu template (avoids the default left icon-gutter that clipped text) plus
        // implicit MenuItem/Separator styles that cascade to submenu items. The MenuItem template
        // reserves a left gutter for check marks and a right column for shortcut text, matching the
        // native Win11 menu layout.
        private const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                    xmlns:sc='clr-namespace:System.Windows.Controls;assembly=PresentationFramework'>
  <SolidColorBrush x:Key='DMenuBg' Color='#2B2B2B'/>
  <SolidColorBrush x:Key='DMenuFg' Color='#F0F0F0'/>
  <SolidColorBrush x:Key='DMenuHover' Color='#3D3D40'/>
  <SolidColorBrush x:Key='DMenuBorder' Color='#454545'/>
  <SolidColorBrush x:Key='DMenuGesture' Color='#9A9A9A'/>

  <Style x:Key='DarkContextMenu' TargetType='ContextMenu'>
    <Setter Property='Foreground' Value='{StaticResource DMenuFg}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <Border Background='{StaticResource DMenuBg}' BorderBrush='{StaticResource DMenuBorder}' BorderThickness='1' CornerRadius='6' Padding='2' MinWidth='150' SnapsToDevicePixels='True'>
            <ItemsPresenter/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Menus style separators via this KEYED style (not the implicit TargetType style), so it must
       be keyed with MenuItem.SeparatorStyleKey or the default theme separator wins. -->
  <Style x:Key='{x:Static sc:MenuItem.SeparatorStyleKey}' TargetType='{x:Type Separator}'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Separator}'>
          <Border Height='1' Margin='6,3' Background='{StaticResource DMenuBorder}' SnapsToDevicePixels='True'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='MenuItem'>
    <Setter Property='Foreground' Value='{StaticResource DMenuFg}'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Border x:Name='Bd' Background='{TemplateBinding Background}' CornerRadius='4' Margin='2,0' SnapsToDevicePixels='True'>
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='22'/>
                <ColumnDefinition Width='*'/>
                <ColumnDefinition Width='Auto'/>
                <ColumnDefinition Width='Auto'/>
              </Grid.ColumnDefinitions>
              <Path x:Name='Check' Grid.Column='0' Width='10' Height='8' VerticalAlignment='Center' HorizontalAlignment='Center'
                    Stretch='Uniform' Data='M0,4.5 L3.5,8 L10,0' Stroke='{StaticResource DMenuFg}' StrokeThickness='1.5' Visibility='Collapsed'/>
              <ContentPresenter Grid.Column='1' ContentSource='Header' Margin='0,4,16,4' VerticalAlignment='Center' RecognizesAccessKey='True'/>
              <TextBlock x:Name='Gesture' Grid.Column='2' Text='{TemplateBinding InputGestureText}' Margin='0,0,10,0' VerticalAlignment='Center' Foreground='{StaticResource DMenuGesture}'/>
              <Path x:Name='Arrow' Grid.Column='3' Margin='0,0,8,0' VerticalAlignment='Center' Data='M0,0 L4,4 L0,8 Z' Fill='{StaticResource DMenuFg}' Visibility='Collapsed'/>
              <Popup x:Name='PART_Popup' Placement='Right' HorizontalOffset='-2' IsOpen='{TemplateBinding IsSubmenuOpen}' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
                <Border Background='{StaticResource DMenuBg}' BorderBrush='{StaticResource DMenuBorder}' BorderThickness='1' CornerRadius='6' Padding='2' MinWidth='150'>
                  <ItemsPresenter/>
                </Border>
              </Popup>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='Role' Value='SubmenuHeader'>
              <Setter TargetName='Arrow' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Check' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsHighlighted' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='{StaticResource DMenuHover}'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Foreground' Value='#808080'/>
              <Setter TargetName='Gesture' Property='Foreground' Value='#6A6A6A'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
    }
}
