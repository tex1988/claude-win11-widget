# ClaudeWidget

A Windows 11 Widgets board widget that displays [claude.ai](https://claude.ai) usage
stats (session %, weekly %, Fable %) in real time, along with reset countdowns,
subscription/billing info, and pay-as-you-go credit controls. Built with WinUI 3 /
Windows App SDK.

## Features

- Session, weekly, and Fable usage progress bars with color-coded thresholds
- Reset countdowns for each usage bucket
- Next payment date / billing interval display
- Pay-as-you-go "usage credits" toggle and monthly spend-limit editor, right from the widget card
- Promotional credit balance display
- English, Russian, and Ukrainian localization (auto-detected from Windows display language)

## Getting started

See [`CLAUDE.md`](CLAUDE.md) for full architecture notes, build/deploy instructions,
and known pitfalls.

Quick start (Windows, requires the .NET 8 SDK and Windows App SDK workload):

```powershell
Copy-Item local.settings.ps1.example local.settings.ps1
# edit local.settings.ps1 and set $CertThumbprint to your local signing cert's thumbprint

powershell -File build.ps1
powershell -File deploy.ps1
powershell -File killwidget.ps1
```

## License

MIT — see [LICENSE](LICENSE).
