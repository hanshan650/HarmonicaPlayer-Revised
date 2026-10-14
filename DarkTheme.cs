using System.Windows;
using System.Windows.Markup;

namespace HarmonicaPlayer;

// 深色科技风主题：一套集中定义的资源字典，由 PlayerWindow 在构造时挂到自己的 Resources 上。
// 之所以把 XAML 内嵌成字符串而不是放 .xaml 文件：测试工程用 <Compile Include="../../*.cs" />
// 直接复用根目录源码，内嵌字符串在正式程序与测试程序里行为一致，既不需要改两处 csproj，
// 也不依赖 pack URI 里的程序集名。注意 ParserTests 只显式列出部分文件，因此不会编译到本文件。
internal static class DarkTheme
{
    private static ResourceDictionary? cached;

    public static ResourceDictionary Dictionary => cached ??= (ResourceDictionary)XamlReader.Parse(Xaml);

    private const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <!-- ===== 调色板：深色科技风 ===== -->
  <SolidColorBrush x:Key="P.Page"        Color="#0A0E13"/>
  <SolidColorBrush x:Key="P.Card"        Color="#121821"/>
  <SolidColorBrush x:Key="P.CardSoft"    Color="#161E28"/>
  <SolidColorBrush x:Key="P.Field"       Color="#0D131A"/>
  <SolidColorBrush x:Key="P.Line"        Color="#232F3D"/>
  <SolidColorBrush x:Key="P.LineSoft"    Color="#1B2531"/>
  <SolidColorBrush x:Key="P.Hover"       Color="#1A2430"/>
  <SolidColorBrush x:Key="P.Text"        Color="#E6EDF3"/>
  <SolidColorBrush x:Key="P.Muted"       Color="#8B9AAB"/>
  <SolidColorBrush x:Key="P.Accent"      Color="#22D3EE"/>
  <SolidColorBrush x:Key="P.AccentDim"   Color="#16323B"/>
  <SolidColorBrush x:Key="P.Sel"         Color="#3322D3EE"/>
  <SolidColorBrush x:Key="P.Thumb"       Color="#2C3B4C"/>
  <SolidColorBrush x:Key="P.ThumbHover"  Color="#3E5568"/>
  <SolidColorBrush x:Key="P.Accent2"     Color="#5EEAD4"/>
  <SolidColorBrush x:Key="P.AccentSoft"  Color="#2422D3EE"/>
  <SolidColorBrush x:Key="P.AccentLine"  Color="#6622D3EE"/>
  <SolidColorBrush x:Key="P.Line2"       Color="#2F3E50"/>
  <SolidColorBrush x:Key="P.Ghost"       Color="#14FFFFFF"/>

  <!-- 卡片与主色按钮用渐变而非纯色，层次更接近参考站的玻璃质感 -->
  <LinearGradientBrush x:Key="P.CardGrad" StartPoint="0,0" EndPoint="0,1">
    <GradientStop Color="#18202B" Offset="0"/>
    <GradientStop Color="#0F161E" Offset="1"/>
  </LinearGradientBrush>
  <LinearGradientBrush x:Key="P.AccentGrad" StartPoint="0,0" EndPoint="1,1">
    <GradientStop Color="#22D3EE" Offset="0"/>
    <GradientStop Color="#5EEAD4" Offset="1"/>
  </LinearGradientBrush>

  <!-- ===== 滚动条：只保留滑块，去掉系统箭头按钮 ===== -->
  <Style x:Key="ThemeThumb" TargetType="Thumb">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Thumb">
          <Border x:Name="b" CornerRadius="4" Background="{StaticResource P.Thumb}" Margin="2"/>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="b" Property="Background" Value="{StaticResource P.ThumbHover}"/>
            </Trigger>
            <Trigger Property="IsDragging" Value="True">
              <Setter TargetName="b" Property="Background" Value="{StaticResource P.Accent}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="ThemePageButton" TargetType="RepeatButton">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Focusable" Value="False"/>
    <Setter Property="IsTabStop" Value="False"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="RepeatButton">
          <Border Background="Transparent"/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ScrollBar">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Width" Value="10"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ScrollBar">
          <Grid Background="Transparent">
            <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="True">
              <Track.DecreaseRepeatButton>
                <RepeatButton Style="{StaticResource ThemePageButton}" Command="ScrollBar.PageUpCommand"/>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb Style="{StaticResource ThemeThumb}"/>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Style="{StaticResource ThemePageButton}" Command="ScrollBar.PageDownCommand"/>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
              <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property="Orientation" Value="Horizontal">
        <Setter Property="Width" Value="Auto"/>
        <Setter Property="Height" Value="10"/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <!-- ===== 输入框 ===== -->
  <Style TargetType="TextBox">
    <Setter Property="Background" Value="{StaticResource P.Field}"/>
    <Setter Property="Foreground" Value="{StaticResource P.Text}"/>
    <Setter Property="BorderBrush" Value="{StaticResource P.Line}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="7,5"/>
    <Setter Property="CaretBrush" Value="{StaticResource P.Accent}"/>
    <Setter Property="SelectionBrush" Value="{StaticResource P.Accent}"/>
    <Setter Property="SelectionOpacity" Value="0.32"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="TextBox">
          <Border x:Name="bd" CornerRadius="6" Background="{TemplateBinding Background}"
                  BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}">
            <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"
                          VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource P.ThumbHover}"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocusWithin" Value="True">
              <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource P.Accent}"/>
              <Setter TargetName="bd" Property="Background" Value="{StaticResource P.CardSoft}"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.5"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== 复选框 ===== -->
  <Style TargetType="CheckBox">
    <Setter Property="Foreground" Value="{StaticResource P.Text}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="CheckBox">
          <StackPanel Orientation="Horizontal" Background="Transparent">
            <Border x:Name="box" Width="17" Height="17" CornerRadius="5" VerticalAlignment="Center"
                    Background="{StaticResource P.Field}" BorderBrush="{StaticResource P.Line}" BorderThickness="1">
              <Path x:Name="tick" Data="M 2 6.4 L 5.4 9.6 L 11.4 2.8" Stroke="{StaticResource P.Accent}"
                    StrokeThickness="2" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                    HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/>
            </Border>
            <ContentPresenter Margin="9,0,0,0" VerticalAlignment="Center"/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="tick" Property="Visibility" Value="Visible"/>
              <Setter TargetName="box" Property="BorderBrush" Value="{StaticResource P.Accent}"/>
              <Setter TargetName="box" Property="Background" Value="{StaticResource P.AccentDim}"/>
            </Trigger>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="box" Property="BorderBrush" Value="{StaticResource P.Accent}"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.48"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== 列表：深色圆角外框。主窗口曲库自己置了透明无边框，所以只影响未定制的列表。 ===== -->
  <Style TargetType="ListBox">
    <Setter Property="Background" Value="{StaticResource P.Field}"/>
    <Setter Property="BorderBrush" Value="{StaticResource P.Line}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Foreground" Value="{StaticResource P.Text}"/>
    <Setter Property="Padding" Value="2"/>
    <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ListBox">
          <Border CornerRadius="8" Background="{TemplateBinding Background}"
                  BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}">
            <ScrollViewer Focusable="False" Padding="{TemplateBinding Padding}"
                          HorizontalScrollBarVisibility="{TemplateBinding ScrollViewer.HorizontalScrollBarVisibility}"
                          VerticalScrollBarVisibility="{TemplateBinding ScrollViewer.VerticalScrollBarVisibility}">
              <ItemsPresenter/>
            </ScrollViewer>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== 列表项（曲库） ===== -->
  <Style TargetType="ListBoxItem">
    <Setter Property="Foreground" Value="{StaticResource P.Text}"/>
    <Setter Property="Padding" Value="9,7"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ListBoxItem">
          <Border x:Name="bd" Background="Transparent" CornerRadius="6"
                  Padding="{TemplateBinding Padding}" Margin="0,1">
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="Background" Value="{StaticResource P.Hover}"/>
            </Trigger>
            <Trigger Property="IsSelected" Value="True">
              <Setter TargetName="bd" Property="Background" Value="{StaticResource P.Sel}"/>
              <Setter Property="Foreground" Value="{StaticResource P.Accent}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== 按钮：胶囊圆角 + 悬停叠加层提亮，主/次/危险三种底色都适用 ===== -->
  <Style TargetType="Button">
    <Setter Property="Foreground" Value="{StaticResource P.Text}"/>
    <Setter Property="Background" Value="{StaticResource P.CardSoft}"/>
    <Setter Property="BorderBrush" Value="{StaticResource P.Line}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="14,7"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Grid>
            <Border x:Name="bd" CornerRadius="999" Background="{TemplateBinding Background}"
                    BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"/>
            <Border x:Name="glow" CornerRadius="999" Background="#FFFFFF" Opacity="0"/>
            <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="glow" Property="Opacity" Value="0.08"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="glow" Property="Opacity" Value="0.17"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource P.Accent}"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== 窗口按钮：无边框窗口自绘的控制按钮。故意不复用上面的胶囊样式，
             避免和全局按钮样式耦合（它必须能覆盖为方形悬停块）。 ===== -->
  <Style x:Key="TitleBarButton" TargetType="Button">
    <Setter Property="Foreground" Value="{StaticResource P.Text}"/>
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="Focusable" Value="False"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="7">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#1EFFFFFF"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#3DFFFFFF"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="TitleBarCloseButton" TargetType="Button">
    <Setter Property="Foreground" Value="{StaticResource P.Text}"/>
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="Focusable" Value="False"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="7">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#E14D63"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#C33450"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== 下拉框（演奏音轨） ===== -->
  <Style TargetType="ComboBoxItem">
    <Setter Property="Foreground" Value="{StaticResource P.Text}"/>
    <Setter Property="Padding" Value="9,6"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="bd" Background="Transparent" CornerRadius="4" Margin="3,1"
                  Padding="{TemplateBinding Padding}">
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="Background" Value="{StaticResource P.Hover}"/>
            </Trigger>
            <Trigger Property="IsHighlighted" Value="True">
              <Setter TargetName="bd" Property="Background" Value="{StaticResource P.Sel}"/>
            </Trigger>
            <Trigger Property="IsSelected" Value="True">
              <Setter Property="Foreground" Value="{StaticResource P.Accent}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ComboBox">
    <Setter Property="Foreground" Value="{StaticResource P.Text}"/>
    <Setter Property="Background" Value="{StaticResource P.Field}"/>
    <Setter Property="BorderBrush" Value="{StaticResource P.Line}"/>
    <Setter Property="Padding" Value="8,5"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBox">
          <Grid>
            <ToggleButton x:Name="toggle" Focusable="False" ClickMode="Press"
                          IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
              <ToggleButton.Template>
                <ControlTemplate TargetType="ToggleButton">
                  <Border x:Name="bd" CornerRadius="6" Background="{StaticResource P.Field}"
                          BorderBrush="{StaticResource P.Line}" BorderThickness="1">
                    <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,10,0"
                          Data="M 0 0 L 4.5 4.5 L 9 0" Stroke="{StaticResource P.Muted}"
                          StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource P.Accent}"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible="False" VerticalAlignment="Center" HorizontalAlignment="Left"
                              Margin="9,0,28,0"
                              Content="{TemplateBinding SelectionBoxItem}"
                              ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                              ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}"/>
            <Popup x:Name="PART_Popup" AllowsTransparency="True" Placement="Bottom" Focusable="False"
                   IsOpen="{TemplateBinding IsDropDownOpen}">
              <Border Background="{StaticResource P.Card}" BorderBrush="{StaticResource P.Line}"
                      BorderThickness="1" CornerRadius="6" Margin="0,4,0,0"
                      MinWidth="{TemplateBinding ActualWidth}" MaxHeight="{TemplateBinding MaxDropDownHeight}">
                <ScrollViewer>
                  <ItemsPresenter/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== 音量滑杆 ===== -->
  <Style TargetType="Slider">
    <Setter Property="Foreground" Value="{StaticResource P.Accent}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Slider">
          <Grid VerticalAlignment="Center" Height="20" Background="Transparent">
            <Border Height="5" CornerRadius="2.5" VerticalAlignment="Center"
                    Background="{StaticResource P.LineSoft}" BorderBrush="{StaticResource P.Line}" BorderThickness="1"/>
            <Track x:Name="PART_Track">
              <Track.DecreaseRepeatButton>
                <RepeatButton Style="{StaticResource ThemePageButton}" Command="Slider.DecreaseLarge"/>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb Width="15" Height="15">
                  <Thumb.Template>
                    <ControlTemplate TargetType="Thumb">
                      <Ellipse Fill="{StaticResource P.Accent}" Stroke="{StaticResource P.Page}" StrokeThickness="2.5"/>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Style="{StaticResource ThemePageButton}" Command="Slider.IncreaseLarge"/>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== 试听进度条 ===== -->
  <Style TargetType="ProgressBar">
    <Setter Property="Foreground" Value="{StaticResource P.Accent}"/>
    <Setter Property="Background" Value="{StaticResource P.LineSoft}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ProgressBar">
          <Border CornerRadius="4" Background="{TemplateBinding Background}" ClipToBounds="True">
            <Grid>
              <Rectangle x:Name="PART_Track"/>
              <Rectangle x:Name="PART_Indicator" Fill="{TemplateBinding Foreground}" HorizontalAlignment="Left"/>
            </Grid>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ===== 工具提示 ===== -->
  <Style TargetType="ToolTip">
    <Setter Property="Foreground" Value="{StaticResource P.Text}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ToolTip">
          <Border Background="{StaticResource P.Card}" BorderBrush="{StaticResource P.Line}"
                  BorderThickness="1" CornerRadius="6" Padding="9,6">
            <ContentPresenter/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

</ResourceDictionary>
""";
}
