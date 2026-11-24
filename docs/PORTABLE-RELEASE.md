# Portable package with diagnostic recording

После последнего проекта прогон завершает очистку соединения ENCY и сообщает
`Completed`. Зависшее освобождение нативного соединения ограничено пятью секундами;
результаты и видео к этому моменту уже сохранены. Монитор останавливает прогон
по конечному статусу, даже если процесс тестера ещё завершает работу. Окно
монитора остаётся доступным для просмотра результатов.

Для запуска распакуйте **весь ZIP** в папку с правом записи и откройте
`ProjectTester/ProjectTesterMonitor.exe`. Папка `tools` должна оставаться рядом
с EXE. Отдельно устанавливать FFmpeg или .NET пользователю не требуется.
ENCY должен быть установлен. Во время диагностического повтора оставьте
сеанс Windows открытым и разблокированным: записывается рабочий стол.

**При обновлении сохраните `ProjectTesterData/settings.json` из рабочей версии.**
Если распаковали архив в новую папку, перенесите туда свой `ProjectTesterData`
или настройте вход заново через Settings. Пользовательские логины и пароли в
архив не включаются. Без этих настроек тестер не сможет получить проекты и
сообщит об ошибке авторизации до запуска ENCY. Не отправляйте `settings.json`
вместе с диагностикой: он может содержать ваши учётные данные.

Если проект не прошёл тест, утилита автоматически повторяет его **ещё один раз**
с тем же исходным файлом и настройками расчёта/симуляции. FFmpeg записывает обе
попытки: при ошибке сохраняются видео первого прогона и повтора, независимо
от результата повтора. По умолчанию видео успешного первого прогона удаляется;
для расследования ложного успешного результата включите сохранение всех записей.
Первоначальная ошибка остаётся в результате, поэтому удачный повтор не скрывает
нестабильный тест. Журналы, настройки без секретов, результаты обеих попыток и
видео находятся в отдельной папке `ProjectTesterData/diagnostics`, за
пределами папок сортировки результатов. В карточке результата есть ссылки на
материалы диагностики.

В результатах различаются ошибки операций, незавершённая проверка и сбой
открытия/связи с ENCY. Если первая попытка прервалась до проверки операций,
а повтор прошёл, проект показывается как **Passed on retry** с сохранённой
историей первого сбоя. Настоящая ошибка операции остаётся ошибкой даже после
успешного повтора. Старые результаты такого же формата читаются с этим правилом.
**Check incomplete** означает, что проверка не завершена; такой результат не
попадает в успешные. Снимки состояний отдельных фаз доступны в раскрывающихся
блоках Stage, а предупреждения записи показываются отдельно от результата теста.

Фоновая проверка выключена по умолчанию (`ValidateBackgroundSimulation=false`)
и может быть включена в Settings.
Для неё ENCY запускается в обычном режиме: `/JOB_MODE` применяется только при
отключённой фоновой проверке. После расчёта сохраняются состояния операций до
и после фоновой/основной симуляции; обнаруженная ошибка не теряется при сбросе
результатов следующей фазы. Если завершение фоновой проверки не подтверждено,
результат будет **Check incomplete**.

Запись захватывает виртуальный рабочий стол со всеми мониторами. Утилита
пытается вывести свою вкладку проекта на передний план и сообщает, если это
не удалось. Прямое переключение вкладки общей оболочки Prime проверено для
одного работающего backend ENCY; при нескольких экземплярах автоматический
выбор вкладки не гарантируется, предупреждение сохраняется в результате.

Для отдельного локального прогона без загрузки проекта с сервера используйте
извлечённый `ProjectTesterData/ProjectTestingUtility.exe`:

```powershell
.\ProjectTesterData\ProjectTestingUtility.exe "C:\Program Files\ENCY Software\ENCY NB 3\Bin64\ency.exe" --project-file "C:\Projects\2097.stcp" --project-id 2097 --keep-recordings
```

Путь к ENCY и исходному проекту должен соответствовать вашей машине. Прогон
использует копию файла и настройки рядом с tester EXE. `--keep-recordings`
сохраняет видео также при успешном результате.

Extract the entire ZIP to a writable folder and run `ProjectTester/ProjectTesterMonitor.exe`.
Keep the `tools` folder alongside the monitor executable. FFmpeg runs from
`tools/ffmpeg/ffmpeg.exe`; it requires no separate installation, administrator
privileges, PATH changes or download on the user's machine. The monitor and
tester include their .NET runtime. ENCY must already be installed and configured.

The monitor extracts the tester and presets into `ProjectTesterData` next to
the monitor. Recording works in an interactive, unlocked Windows desktop session.
Keep that session active during the diagnostic retry; the video captures the desktop.

### Проверка изображения при записи

Записывающий процесс проверяет изображение раз в секунду. Чёрный фон с одним
курсором больше не считается подтверждённым захватом. Если GDI при запуске не
даёт изображения, утилита пробует DXGI Desktop Duplication на системах с одним
активным монитором. При нескольких мониторах GDI продолжает снимать весь рабочий
стол; автоматический переход только на экран 0 не выполняется.

Длительный чёрный участок или отсутствие данных проверки отмечаются отдельной
ошибкой записи. Результат расчёта ENCY остаётся самостоятельным; видео с такой
ошибкой сохраняется даже при успешной первой попытке. Диагностика включает
`capture-health.json`, `capture-selection.json` и `capture-environment-*.json`.
При выборе резервного захвата его видео находится в `capture-ddagrab/desktop.mkv`;
кнопка в результате открывает выбранную запись, исходная проба остаётся рядом.

Для короткой проверки обоих способов на тестовой машине запустите включённый
`Test-DesktopRecording.ps1`; инструкция — `RDP-RECORDING.md`. Проверка пикселей
обнаруживает чёрное изображение, но не доказывает правильность показанного окна.
Работу после блокировки Windows или отключения удалённого сеанса необходимо
проверить в условиях конкретной машины.

## Build the package

With the .NET 10 SDK and access to the configured ENCY SDK NuGet feed, run
from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Portable.ps1
```

The script downloads the pinned FFmpeg 8.1.2 essentials build from the Gyan
GitHub mirror, verifies its SHA-256 against the checked-in digest, checks
`libx264` and `gdigrab`, publishes the current tester, embeds that freshly
published executable into the monitor, and creates a ZIP and its SHA-256 file
under `artifacts/releases`. It rejects nonempty login/password/CamlmAuthKey fields in all
embedded settings presets. It does not change the old executable checked into
`ProjectTestingMonitor/Resources`.

Publishing also runs the actual self-contained tester in an isolated folder:
missing launch arguments and empty credentials must fail with exit code 1 and
an explicit `run-status.json`. The check uses a synthetic CAM filename and
does not start ENCY or authenticate against an external server.

To prepare FFmpeg for development builds without publishing, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Prepare-FFmpeg.ps1
```

The monitor project copies `artifacts/third-party/ffmpeg` into its output and
publish directories. For a standalone tester build, copy that folder to
`tools/ffmpeg` next to `ProjectTestingUtility.exe`. Downloaded archives, binaries
and release files are ignored by Git. The release script fails if a required
tool or license file is missing; there is no silent download at application startup.

## FFmpeg provenance and redistribution

The package carries the original `LICENSE`, `README.txt`, a version/configuration
dump, a manifest with pinned URLs and SHA-256 digests, and `NOTICE.txt` under
`tools/ffmpeg`. This is a static GPLv3 build, including libx264. The monitor invokes
it as a separate executable.

The generated package can be used for local/internal validation. Before external
distribution, provide the exact complete corresponding source of FFmpeg and the
linked libraries, plus build scripts/patches and applicable licenses, under GPLv3
section 6. A link to the FFmpeg repository alone is insufficient for this static
build. Obtain the complete matching sources from the binary publisher, or build
from retained sources and ship those alongside the binary. The bundled notice
states this explicitly; it does not make an unsupported written source offer.

If an installer is added later, install the complete `tools/ffmpeg` folder next
to the monitor, including these notices. Users still need no separate FFmpeg setup.
