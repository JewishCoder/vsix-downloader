# Архитектура VSIX Downloader

## Сценарий

```text
Интерактивный терминал Spectre.Console
    │ Extension ID
    ▼
MarketplaceClient ── POST extensionquery ──► Visual Studio Marketplace
    │ Версии и опубликованные сборки
    ▼
Выбор конкретной версии, платформ и папки
    │ По одному пакету на выбранную сборку
    ▼
DownloadService ── GET VSIXPackage ──► Marketplace ── HTTPS redirect ──► CDN
    │ Поток, повтор, Range при доступной докачке
    ▼
.part ── проверка формата и идентичности ──► итоговый .vsix
```

Решения MVP: .NET 11, Windows/Linux, только интерактивный режим, только публичный Visual Studio Marketplace, без авторизации на Marketplace и в прокси, без установки расширений. Утилита не вычисляет совместимость расширения с конкретной версией редактора, не загружает зависимости и не проверяет цифровую подпись Marketplace. Проверка VSIX сверяет структуру контейнера и `publisher`, `id`, `version`, `targetPlatform` в манифесте.

## Контракт Microsoft

- Метаданные читаются через `POST https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery?api-version=7.2-preview.1`. Запрос фильтрует по точному `publisher.extension` и цели `Microsoft.VisualStudio.Code`. Запрашиваются версии, свойства версий и адреса ресурсов, не прошедшие проверку версии исключаются. Признак pre-release берётся из `Microsoft.VisualStudio.Code.PreRelease`. [Официальный Gallery REST client](https://github.com/microsoft/azure-devops-extension-api/blob/master/src/Gallery/GalleryClient.ts#L837-L857) · [Флаги запроса Microsoft](https://learn.microsoft.com/en-us/javascript/api/azure-devops-extension-api/extensionqueryflags)
- Из `fallbackAssetUri` выбранной версии берётся адрес `Microsoft.VisualStudio.Services.VSIXPackage`. Утилита добавляет `redirect=true`, а для платформенного пакета — `targetPlatform`. Так же строит адрес официальный клиент VS Code. Страницы сайта не разбираются. Контракт `preview` изолирован в `MarketplaceClient`, чтобы возможное изменение запроса не затронуло TUI и скачивание. [Код загрузки в VS Code](https://github.com/microsoft/vscode/blob/main/src/vs/platform/extensionManagement/common/extensionGalleryService.ts#L314-L320) · [Публикация платформенных VSIX](https://code.visualstudio.com/api/working-with-extensions/publishing-extension#platformspecific-extensions)

## Транспорт и докачка

- Общий `HttpClient` использует `SocketsHttpHandler`. Без заданного прокси используется прямое соединение. Сетевые ошибки Marketplace, прокси и TLS различаются в интерфейсе. Проверка TLS не отключается. Пакеты загружаются последовательно, чтобы не делить пропускную способность медленного прокси между несколькими потоками.
- Чтение тела начинается после получения заголовков и идёт через буфер фиксированного размера. Таймаут бездействия устанавливается на очередное чтение, а не на весь файл. При временных сетевых сбоях и HTTP `408`, `429`, `500`, `502`, `503`, `504` загрузчик повторяет запрос, учитывая `Retry-After`.
- Поддерживается докачка после обрыва и перезапуска. Сохраняется сильный `ETag` или, при отсутствии ETag, достаточно старый по отношению к `Date` заголовок `Last-Modified`. Дописывание разрешается только к `206` с корректным началом и длиной диапазона, ожидаемой общей длиной, если она известна, и тем же конечным HTTPS-ресурсом. Полный `200` заменяет частичный файл. `416` и неподходящий `206` приводят к новому запросу с начала. [RFC 9110, If-Range и Range](https://www.rfc-editor.org/rfc/rfc9110.html#name-if-range)
- Служебные файлы расположены рядом с конечным файлом, чтобы публикация была переименованием в пределах того же тома. Блокировка предотвращает одновременную запись двух экземпляров программы в один пакет. Итоговый путь не появляется, пока не проверен манифест. Ошибка одной сборки не отменяет обработку оставшихся.
