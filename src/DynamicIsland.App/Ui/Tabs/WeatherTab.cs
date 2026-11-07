using DynamicIsland.Features;
using SkiaSharp;

namespace DynamicIsland.Ui.Tabs;

/// <summary>Значки погоды, нарисованные кистями: без шрифтов и картинок.</summary>
internal static class WeatherIcons
{
    public static void Draw(UiKit ui, int code, bool day, float cx, float cy, float size)
    {
        var k = size / 40f;
        var sun = new SKColor(255, 204, 51);
        var cloud = ui.Pal.Primary.Alpha > 0 && ui.Pal.Primary.Red < 120 ? new SKColor(140, 150, 165) : new SKColor(210, 214, 222);
        var rain = new SKColor(90, 170, 255);

        void Sun(float x, float y, float r)
        {
            ui.Circle(x, y, r, day ? sun : new SKColor(200, 205, 230));
            if (!day) return;
            for (var i = 0; i < 8; i++)
            {
                var a = i * MathF.PI / 4;
                ui.Line(x + MathF.Cos(a) * (r + 3 * k), y + MathF.Sin(a) * (r + 3 * k),
                        x + MathF.Cos(a) * (r + 7 * k), y + MathF.Sin(a) * (r + 7 * k), sun, 2f * k);
            }
        }

        void Cloud(float x, float y, float s)
        {
            ui.Circle(x - 9 * s, y + 3 * s, 8 * s, cloud);
            ui.Circle(x + 1 * s, y - 3 * s, 11 * s, cloud);
            ui.Circle(x + 11 * s, y + 3 * s, 8 * s, cloud);
            ui.RoundRect(new SKRect(x - 15 * s, y + 3 * s, x + 17 * s, y + 11 * s), 4 * s, cloud);
        }

        switch (code)
        {
            case 0:
                Sun(cx, cy, 9 * k);
                break;
            case 1 or 2:
                Sun(cx - 6 * k, cy - 6 * k, 7 * k);
                Cloud(cx + 3 * k, cy + 4 * k, 0.8f * k);
                break;
            case 3:
                Cloud(cx, cy, 1f * k);
                break;
            case 45 or 48:
                Cloud(cx, cy - 4 * k, 0.9f * k);
                for (var i = 0; i < 2; i++)
                    ui.Line(cx - 12 * k, cy + 10 * k + i * 6 * k, cx + 12 * k, cy + 10 * k + i * 6 * k, cloud, 2.2f * k);
                break;
            case >= 71 and <= 77 or 85 or 86:
                Cloud(cx, cy - 5 * k, 0.95f * k);
                foreach (var dx in new[] { -9f, 0f, 9f })
                    ui.Circle(cx + dx * k, cy + 12 * k, 2.2f * k, SKColors.White);
                break;
            case >= 95:
                Cloud(cx, cy - 5 * k, 0.95f * k);
                ui.Line(cx + 2 * k, cy + 6 * k, cx - 3 * k, cy + 13 * k, sun, 2.6f * k);
                ui.Line(cx - 3 * k, cy + 13 * k, cx + 3 * k, cy + 13 * k, sun, 2.6f * k);
                ui.Line(cx + 3 * k, cy + 13 * k, cx - 2 * k, cy + 19 * k, sun, 2.6f * k);
                break;
            default:
                Cloud(cx, cy - 5 * k, 0.95f * k);
                foreach (var dx in new[] { -9f, 0f, 9f })
                    ui.Line(cx + dx * k, cy + 8 * k, cx + (dx - 3) * k, cy + 16 * k, rain, 2.2f * k);
                break;
        }
    }
}

/// <summary>Погода сейчас, ближайшие часы и неделя.</summary>
internal sealed class WeatherTab(WeatherService weather, Configuration.Settings settings) : ITabView
{
    public void Activated() => weather.Refresh();

    public void Draw(UiKit ui)
    {
        var pal = ui.Pal;

        if (!weather.HasCity)
        {
            ui.Text("Город не задан", 0, 30, 13, pal.Primary, bold: true);
            ui.Text("Название вводится вручную: по сети остров его не определяет", 0, 50, 11.5f, pal.Muted);
            ui.Button(new SKRect(0, 66, 150, 92), "Выбрать город", () => CommandBarHost.Toggle("город "), primary: true);
            return;
        }

        if (weather.Now is not { } now)
        {
            ui.Text(weather.Error.Length > 0 ? weather.Error : "Загружаю прогноз…", ui.Width / 2, 100, 13, pal.Muted, SKTextAlign.Center);
            return;
        }

        // Сейчас.
        WeatherIcons.Draw(ui, now.Code, now.IsDay, 30, 40, 50);
        ui.Text(weather.Temp(now.Temp), 66, 54, 34, pal.Primary, bold: true);
        ui.Text(WeatherService.Describe(now.Code), 66, 74, 12.5f, pal.Primary);
        ui.Text($"Ощущается {weather.Temp(now.Feels)} · ветер {now.Wind:0} м/с", 0, 100, 11.5f, pal.Muted);

        var city = ui.Fit(settings.WeatherCityResolved, 11.5f, ui.Width * 0.5f);
        ui.Text(city, ui.Width - 34, 14, 11.5f, pal.Muted, SKTextAlign.Right);
        ui.IconButton(new SKRect(ui.Width - 28, 0, ui.Width, 22), G.Edit, () => CommandBarHost.Toggle("город "));

        // Часы: каждые три часа.
        var hours = weather.Hours.Where((_, i) => i % 3 == 0).Take(6).ToList();
        var hx = 190f;
        var cellW = (ui.Width - hx) / Math.Max(1, hours.Count);
        for (var i = 0; i < hours.Count; i++)
        {
            var h = hours[i];
            var cx = hx + cellW * i + cellW / 2;
            ui.Text(h.Time.ToString("HH:mm"), cx, 52, 10.5f, pal.Muted, SKTextAlign.Center);
            WeatherIcons.Draw(ui, h.Code, h.Time.Hour is >= 6 and < 21, cx, 76, 26);
            ui.Text(weather.Temp(h.Temp), cx, 104, 12, pal.Primary, SKTextAlign.Center, true);
            if (h.RainChance >= 30 && WeatherService.IsPrecipitation(h.Code))
                ui.Text($"{h.RainChance}%", cx, 118, 9.5f, new SKColor(90, 170, 255), SKTextAlign.Center);
        }

        ui.Line(0, 130, ui.Width, 130, pal.Line);

        // Неделя.
        var days = weather.Days.Take(7).ToList();
        var dw = ui.Width / Math.Max(1, days.Count);
        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var cx = dw * i + dw / 2;
            var name = i == 0 ? "Сегодня" : d.Date.ToString("ddd");
            ui.Text(name, cx, 150, 11, i == 0 ? ui.Accent : pal.Muted, SKTextAlign.Center, i == 0);
            WeatherIcons.Draw(ui, d.Code, true, cx, 178, 28);
            ui.Text(weather.Temp(d.Max), cx, 208, 12, pal.Primary, SKTextAlign.Center, true);
            ui.Text(weather.Temp(d.Min), cx, 223, 11, pal.Muted, SKTextAlign.Center);
        }
    }
}
