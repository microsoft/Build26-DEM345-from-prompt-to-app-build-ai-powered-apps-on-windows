using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VideoStudio.Models;

namespace VideoStudio.Controls;

public sealed partial class AddStepButton : UserControl
{
    public static readonly DependencyProperty ContainerEffectsProperty =
        DependencyProperty.Register(nameof(ContainerEffects), typeof(IList<Effect>), typeof(AddStepButton),
            new PropertyMetadata(null, OnEffectsChanged));

    public static readonly DependencyProperty NativeEffectsProperty =
        DependencyProperty.Register(nameof(NativeEffects), typeof(IList<Effect>), typeof(AddStepButton),
            new PropertyMetadata(null, OnEffectsChanged));

    public IList<Effect>? ContainerEffects
    {
        get => (IList<Effect>?)GetValue(ContainerEffectsProperty);
        set => SetValue(ContainerEffectsProperty, value);
    }

    public IList<Effect>? NativeEffects
    {
        get => (IList<Effect>?)GetValue(NativeEffectsProperty);
        set => SetValue(NativeEffectsProperty, value);
    }

    public event EventHandler<Effect>? EffectSelected;

    public AddStepButton()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildFlyoutItems();
    }

    private static void OnEffectsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AddStepButton button)
        {
            button.BuildFlyoutItems();
        }
    }

    private void BuildFlyoutItems()
    {
        EffectsFlyout.Items.Clear();

        var hasContainer = ContainerEffects is { Count: > 0 };
        var hasNative = NativeEffects is { Count: > 0 };

        if (!hasContainer && !hasNative)
            return;

        if (hasContainer)
        {
            AddGroupHeader("LINUX CONTAINER");
            foreach (var effect in ContainerEffects!)
            {
                AddEffectItem(effect);
            }
        }

        if (hasContainer && hasNative)
        {
            EffectsFlyout.Items.Add(new MenuFlyoutSeparator());
        }

        if (hasNative)
        {
            AddGroupHeader("NATIVE AI");
            foreach (var effect in NativeEffects!)
            {
                AddEffectItem(effect);
            }
        }
    }

    private void AddGroupHeader(string text)
    {
        var header = new MenuFlyoutItem
        {
            Text = text,
            IsEnabled = false,
        };
        header.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        header.FontSize = 11;
        EffectsFlyout.Items.Add(header);
    }

    private void AddEffectItem(Effect effect)
    {
        var item = new MenuFlyoutItem
        {
            Text = effect.Name,
            Icon = new FontIcon { Glyph = effect.Icon },
            Tag = effect,
        };
        item.Click += OnEffectItemClick;
        EffectsFlyout.Items.Add(item);
    }

    private void OnEffectItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: Effect effect })
        {
            EffectSelected?.Invoke(this, effect);
        }
    }
}
