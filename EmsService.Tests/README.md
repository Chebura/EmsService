# Unit-тесты EmsService

Проект `EmsService.Tests` содержит unit-тесты сервиса рассылки `EmsService`. Тесты сервиса рассылки
перенесены из `EmailMassSender.Tests` и адаптированы под поведение EmsService.

Список тест-кейсов — в [TEST-CASES.md](TEST-CASES.md).

## Требования

- .NET 10 SDK.
- Доступ к nuget.org для восстановления пакетов.

Реальный SMTP-сервер и доступ в интернет не нужны: используется локальный фейковый SMTP-сервер
(`Infrastructure/FakeSmtpServer.cs`).

## Запуск

Из корня репозитория:

```
dotnet test --solution EmsService.sln
```

Один класс или один тест (фильтры xUnit v3):

```
dotnet test --solution EmsService.sln -- --filter-class EmsService.Tests.EmsWorkerTests
dotnet test --solution EmsService.sln -- --filter-method *S26_*
```

Тесты написаны на xUnit v3 (Microsoft.Testing.Platform), поэтому в корне репозитория лежит `global.json`
с `"test": { "runner": "Microsoft.Testing.Platform" }` — без него `dotnet test` в .NET 10 SDK не запустит проект.

## Структура проекта

| Файл | Назначение |
|------|------------|
| `EmailMassSendingServiceTests.cs` | S01–S22, S28, S29: конструктор, подготовка и обработка задач с выключенным SMTP |
| `EmailMassSendingServiceSmtpTests.cs` | S23–S27, S30: отправка через фейковый SMTP-сервер, повторы, отмена |
| `EmsSettingsStoreTests.cs` | H01–H14: файл настроек, проверка при сохранении и перед запуском |
| `EmsWorkerTests.cs` | W01–W05: запуск и остановка рассылки фоновым сервисом |
| `Infrastructure/TestWorkspace.cs` | Временная директория, файл текста, конфигурация, чтение и запись файлов задач |
| `Infrastructure/FakeSmtpServer.cs` | Локальный SMTP-сервер: приём писем, отказ, зависание |
