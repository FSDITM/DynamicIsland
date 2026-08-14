# DynamicIsland

Динамический островок для Windows. Переработанная версия
[DynamicWin](https://github.com/FlorianButz/DynamicWin) Флориана Бутца
с новым ядром отрисовки.

## Почему не форк «как есть»

Исходники DynamicWin удалены из репозитория 19 октября 2025 года (коммит
`Remove initial version`, −10 245 строк); там теперь только релизы. Код
восстановлен из истории git и лежит рядом в `../upstream-src` как справочник.

Главная проблема оригинала была не в мелочах, а в архитектуре ядра:

| Что было | Что стало |
|---|---|
| Полноэкранное прозрачное WPF-окно (`AllowsTransparency`) — layered window, весь экран через CPU каждый кадр | Окно-«сцена» размером с островок, `WS_EX_NOREDIRECTIONBITMAP` + DirectComposition, кадр не покидает GPU |
| Софтверный Skia (`SKSurface.Create` поверх `WriteableBitmap`) | Skia на Direct3D 12 через `SkiaSharp.Direct3D.Vortice` |
| 60 fps всегда, даже в покое | Кадр только когда есть что менять; в покое поток спит на нуле CPU |
| WMI-запрос яркости каждый кадр в UI-потоке | Системные метрики — фоновым опросом с кэшем |
| `Vec2` — class, `new List<>` в `Update`/`Draw` каждого объекта каждый кадр | `Vec2` — readonly struct, отрисовка без аллокаций |
| `new SKPaint` / `SKImageFilter` каждый кадр без `Dispose` | Кисти и фильтры кэшируются |

## Замеры

`--selftest` гоняет анимацию и пишет метрики в `dynamicisland.log`.
На AMD Radeon (Ryzen 5 6600H), сцена 1000×300 @ DPI 144:

```
установившийся режим: 59.8 fps, CPU 7.1% одного ядра, 1.19 ms CPU на кадр
первый кадр 68 ms (компиляция шейдеров, разово)
слой валидации D3D12: сообщений нет
в простое: 0 кадров
```

## Сборка и запуск

Нужен .NET SDK 10.

```bash
dotnet run --project src/DynamicIsland.App
```

Самопроверка с метриками:

```bash
dotnet run --project src/DynamicIsland.App -- --selftest 8
```

Выход из приложения — правый клик по островку.

## Устройство

```
src/DynamicIsland.App/
├── Interop/Win32.cs              P/Invoke: окно, монитор, DPI, DComp
├── Platform/OverlayWindow.cs     окно оверлея, hit-test, цикл сообщений
├── Rendering/
│   └── GpuCompositionHost.cs     D3D12 + composition swapchain + DComp + Skia
├── Ui/
│   ├── Vec2.cs                   вектор (struct)
│   ├── SecondOrder.cs            пружинная динамика
│   └── Island.cs                 форма островка и отрисовка
└── Program.cs                    планировщик кадров, HUD, самопроверка
```

### Как устроен кадр

Состоянием back buffer'а управляет только этот код, Skia всегда застаёт ресурс
в объявленном ей состоянии `RenderTarget`:

```
ожидание fence кадра ──► барьер Present → RenderTarget
                    ──► Skia рисует
                    ──► Flush + Submit
                    ──► барьер RenderTarget → Present
                    ──► Present(1)
```

Поэтому обёртки `SKSurface` можно кэшировать на каждый буфер, а не пересоздавать
каждый кадр. Слой валидации D3D12 в отладочной сборке подтверждает, что
состояния сходятся.

## Лицензия

Производная работа от [DynamicWin](https://github.com/FlorianButz/DynamicWin)
(© Florian Butz), распространяемого под
[CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/).
Этот проект наследует ту же лицензию: **CC BY-SA 4.0** — с указанием авторства
и распространением производных на тех же условиях.
