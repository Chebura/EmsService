# EMS Service

[![Windows installer](https://github.com/Chebura/EmsService/actions/workflows/installer.yml/badge.svg)](https://github.com/Chebura/EmsService/actions/workflows/installer.yml)

**Скачать установщик для Windows (x64):
[EmsService.msi](https://github.com/Chebura/EmsService/releases/latest/download/EmsService.msi)**
(все версии — на странице [Releases](https://github.com/Chebura/EmsService/releases)).

Email Mass Sender в виде сервиса ASP.NET Core (.NET 10). Рассылка выполняется фоновым сервисом
(`BackgroundService`) и запускается кнопкой в веб-интерфейсе. Логирование — NLog.

Документация:

- [docs/USER-GUIDE.md](docs/USER-GUIDE.md) — руководство пользователя;
- [srs/SRS.md](srs/SRS.md) — требования к системе;
- [EmsService/release-notes.txt](EmsService/release-notes.txt) — история изменений.

## Возможности

- Веб-интерфейс (HTTP, без аутентификации) для настройки сервиса:
  отправитель, SMTP, повторная отправка, группы получателей и тексты писем.
- Запуск и остановка рассылки кнопками в интерфейсе.
  Рассылка выполняется, пока все письма не будут отправлены либо не будет исчерпано количество попыток
  (или не истечёт срок актуальности группы).
- Просмотр задач рассылки и состояния каждого получателя, журнал последних записей лога.
- Настройки хранятся в файле `emssettings.json` рядом с исполняемым файлом.

## Сборка и публикация

```
dotnet publish EmsService -c Release -o publish
```

Публикуется self-contained single-file приложение для текущей платформы, установленный .NET на целевой машине
не нужен. Для другой платформы укажите RID: `-r win-x64`, `-r linux-x64`, `-r linux-arm64`, `-r osx-arm64`.

На Linux нужна библиотека ICU (`libicu`), которая есть в большинстве дистрибутивов.

## Установщик для Windows

```
dotnet build Installer -c Release
```

Результат — `Installer\bin\Release\EmsService.msi` (собирается только на Windows, WiX Toolset подтягивается из NuGet).
Установщик публикует сервис, ставит его в `Program Files\EMS Service`, регистрирует службу `EmsService`
с автозапуском и перезапуском при сбое и добавляет ярлыки на веб-интерфейс (http://localhost:5080/) на рабочий стол
и в меню «Пуск».
При обновлении и удалении сохраняются `appsettings.json`, `emssettings.json`, тексты, задачи и журналы.

Проект установщика не входит в `EmsService.sln`, чтобы решение собиралось и на Linux.

Установщик собирается автоматически GitHub Actions ([`.github/workflows/installer.yml`](.github/workflows/installer.yml)):
при каждом push и pull request в `main` прогоняются тесты и собирается MSI (доступен в артефактах запуска).
При push в `main` MSI публикуется в релиз `v<Version>` (версия из `EmsService/EmsService.csproj`):
если такого релиза нет, он создаётся, иначе установщик в нём заменяется. Для нового релиза увеличьте `Version`.

## Запуск

Консольно: `ems-service.exe` (Linux: `./ems-service`), затем откройте http://localhost:5080/index.html
(корневой адрес тоже открывает `index.html`). При запуске из IDE или через `dotnet run` браузер открывает
`index.html` автоматически.

Как служба Windows (от имени администратора):

```
sc create EmsService binPath= "C:\ems\ems-service.exe" start= auto
sc start EmsService
```

Как служба systemd (Linux), `/etc/systemd/system/ems.service`:

```
[Unit]
Description=Email Mass Sender service

[Service]
Type=notify
ExecStart=/opt/ems/ems-service
WorkingDirectory=/opt/ems
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

```
sudo systemctl enable --now ems
```

## Тесты

```
dotnet test --solution EmsService.sln
```

Подробности — в [EmsService.Tests/README.md](EmsService.Tests/README.md), список кейсов — в
[EmsService.Tests/TEST-CASES.md](EmsService.Tests/TEST-CASES.md).

## Конфигурация

`appsettings.json`:

| Параметр              | Описание                                                                                   |
|-----------------------|--------------------------------------------------------------------------------------------|
| `Urls`                | Адрес веб-интерфейса. По умолчанию `http://localhost:5080`. Для доступа по сети — `http://0.0.0.0:5080`. |
| `EmsSettingsFilePath` | Файл настроек рассылки (по умолчанию `emssettings.json`). Создаётся при первом запуске.     |

**Внимание:** интерфейс не имеет аутентификации. Открывая его в сеть, ограничьте доступ на уровне сети/firewall.

`nlog.config` — настройка логирования: файлы `logs/ems-<дата>.log`, консоль и буфер для вкладки «Журнал».

Относительные пути (каталог задач, файлы текстов) отсчитываются от каталога исполняемого файла.
