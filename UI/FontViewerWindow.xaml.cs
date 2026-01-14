using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WwDevicesDotNet;

namespace WWCduDcsBiosBridge.UI;

public partial class FontViewerWindow : Window
{
    private McduFontFile? _currentFont;
    private readonly ICdu? _cdu;

    public FontViewerWindow(ICdu? cdu = null)
    {
        InitializeComponent();
        _cdu = cdu;
        if (_cdu != null)
        {
            ShowOnHardwareButton.IsEnabled = true;
        }
        LoadFontList();
        LoadColors();
    }

    private void LoadColors()
    {
        ColorComboBox.ItemsSource = Enum.GetValues(typeof(Colour));
        ColorComboBox.SelectedItem = Colour.Green;
    }

    private void LoadFontList()
    {
        try
        {
            var resourcesPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Resources");
            if (Directory.Exists(resourcesPath))
            {
                var fontFiles = Directory.GetFiles(resourcesPath, "*-font-*.json");
                var fontOptions = fontFiles.Select(f => new KeyValuePair<string, string>(f, System.IO.Path.GetFileName(f))).ToList();
                FontComboBox.ItemsSource = fontOptions;
                if (fontOptions.Count > 0)
                {
                    FontComboBox.SelectedIndex = 0;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading font list: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FontComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontComboBox.SelectedItem is KeyValuePair<string, string> selectedFontFile)
        {
            LoadFont(selectedFontFile.Key);
        }
    }

    private void LoadFont(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            _currentFont = JsonSerializer.Deserialize<McduFontFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (_currentFont != null)
            {
                var characters = _currentFont.LargeGlyphs.Select(g => g.Character).Distinct().ToList();
                CharacterComboBox.ItemsSource = characters;
                if (characters.Count > 0)
                {
                    CharacterComboBox.SelectedIndex = 0;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading font: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CharacterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CharacterComboBox.SelectedItem is char selectedChar)
        {
            RenderGlyphs(selectedChar);
        }
    }

    private void ColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CharacterComboBox.SelectedItem is char selectedChar)
        {
            RenderGlyphs(selectedChar);
        }
    }

    private void RenderGlyphs(char c)
    {
        if (_currentFont == null) return;

        var largeGlyph = _currentFont.LargeGlyphs.FirstOrDefault(g => g.Character == c);
        var smallGlyph = _currentFont.SmallGlyphs.FirstOrDefault(g => g.Character == c);

        RenderGlyph(LargeGlyphCanvas, largeGlyph);
        RenderGlyph(SmallGlyphCanvas, smallGlyph);
    }

    private void ShowOnHardwareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cdu == null || _currentFont == null) return;

        try
        {
            // Apply the current font to the device
            _cdu.UseFont(_currentFont, false);

            _cdu.Screen.Clear();

            if (ColorComboBox.SelectedItem is Colour selectedColour)
            {
                _cdu.Screen.Colour = selectedColour;
            }
            
            int row = 0;
            int col = 0;

            // Header for Large Glyphs
            _cdu.Screen.Goto(row, 0);
            _cdu.Screen.Small = true;
            _cdu.Screen.Write("LARGE GLYPHS:");
            row++;

            _cdu.Screen.Small = false;
            foreach (var glyph in _currentFont.LargeGlyphs)
            {
                if (row >= Metrics.Lines) break;
                
                _cdu.Screen.Goto(row, col);
                _cdu.Screen.Put(glyph.Character);
                
                col++;
                if (col >= Metrics.Columns)
                {
                    col = 0;
                    row++;
                }
            }

            // Move to next row if we were in the middle of one
            if (col != 0)
            {
                col = 0;
                row++;
            }

            if (row < Metrics.Lines - 1)
            {
                // Header for Small Glyphs
                _cdu.Screen.Goto(row, 0);
                _cdu.Screen.Small = true;
                _cdu.Screen.Write("SMALL GLYPHS:");
                row++;

                foreach (var glyph in _currentFont.SmallGlyphs)
                {
                    if (row >= Metrics.Lines) break;

                    _cdu.Screen.Goto(row, col);
                    _cdu.Screen.Put(glyph.Character);

                    col++;
                    if (col >= Metrics.Columns)
                    {
                        col = 0;
                        row++;
                    }
                }
            }

            _cdu.RefreshDisplay();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error showing on hardware: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RenderGlyph(Canvas canvas, McduFontGlyph? glyph)
    {
        canvas.Children.Clear();
        if (glyph == null || glyph.BitArray == null || glyph.BitArray.Length == 0)
        {
            canvas.Width = 10;
            canvas.Height = 10;
            return;
        }

        int rows = glyph.BitArray.Length;
        int cols = glyph.BitArray[0].Length;

        canvas.Width = cols;
        canvas.Height = rows;

        var brush = Brushes.LimeGreen;
        if (ColorComboBox.SelectedItem is Colour selectedColour)
        {
            brush = selectedColour switch
            {
                Colour.Amber => Brushes.Orange,
                Colour.Brown => Brushes.SaddleBrown,
                Colour.Cyan => Brushes.Cyan,
                Colour.Grey => Brushes.Gray,
                Colour.Green => Brushes.LimeGreen,
                Colour.Khaki => Brushes.Khaki,
                Colour.Magenta => Brushes.Magenta,
                Colour.Red => Brushes.Red,
                Colour.White => Brushes.White,
                Colour.Yellow => Brushes.Yellow,
                _ => Brushes.LimeGreen
            };
        }

        for (int y = 0; y < rows; y++)
        {
            string row = glyph.BitArray[y];
            for (int x = 0; x < row.Length; x++)
            {
                if (row[x] == '1' || row[x] == 'X')
                {
                    var rect = new Rectangle
                    {
                        Width = 1,
                        Height = 1,
                        Fill = brush
                    };
                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect, y);
                    canvas.Children.Add(rect);
                }
            }
        }
    }
}
