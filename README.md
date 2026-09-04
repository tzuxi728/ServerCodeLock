# ServerCodeLock

> One-time wipe PIN gate for private Rust servers.

![Version](https://img.shields.io/badge/version-1.3.0-blue)
![License](https://img.shields.io/badge/license-MIT-green)
![Framework](https://img.shields.io/badge/framework-Oxide%20%7C%20Carbon-orange)
![Game](https://img.shields.io/badge/game-Rust-red)

**Author:** [tzuxi728](https://github.com/tzuxi728) · **Telegram:** [@tzuxi](https://t.me/tzuxi)

A lightweight server-side lock that asks every non-whitelisted player for a
numeric PIN on join. Works with both **Oxide** and **Carbon**. Designed for
private/whitelisted servers: perfect for wipe-gated communities.

---

## ✨ Features

- 🔒 Numeric PIN gate shown on join (or after sleep/respawn)
- 🧹 **One-time per wipe** — the allow-list resets on every new save/wipe
- 👥 Per-player allow-list (`grant` / `revoke`) with persistence
- 🚨 Attempt limits → kick, then ban
- ⏱ Idle timeout auto-kick
- 🛡 Anti-cheat pause + snap-back while the gate is open
- 🧊 Blocks damage, targeting, looting, building, chat and more while gated
- 🌐 Bilingual UI (English / Russian)
- 📝 File logging and verbose debug mode

---

## 📥 Installation

**1. Copy the plugin file**

- **Oxide:** put `ServerCodeLock.cs` into `oxide/plugins/`
- **Carbon:** put `ServerCodeLock.cs` into `carbon/plugins/`

**2. Load / reload**

```
oxide.reload ServerCodeLock        # Oxide
carbon.plugin reload ServerCodeLock # Carbon
```

If you're updating an already-loaded plugin, do a clean reload so the server
recompiles the new source:

```
oxide.plugin unload ServerCodeLock && oxide.plugin load ServerCodeLock   # Oxide
carbon.plugin unload ServerCodeLock && carbon.plugin load ServerCodeLock # Carbon
```

**3. Set your PIN** (otherwise the default `1234` is used and a warning is printed):

```
scl.setpass 4829
```

**4. Done.** Any player **not** in the allow-list sees the keypad on join and must
enter the PIN. Admins (config `BypassAdmins`) skip it by default.

---

### 💾 Data & reset (important)

The allow-list is stored in `ServerCodeLock/state.json` (fields `WipeId`,
`Authorized`, `Attempts`). Data path depends on the framework:

- **Oxide:** `oxide/data/ServerCodeLock/state.json`
- **Carbon:** `carbon/data/ServerCodeLock/state.json`

> ⚠️ Deleting only a legacy `authorized` file does **not** clear the list — it lives
> inside `state.json`. To fully reset, delete the whole `ServerCodeLock` data
> folder **or** use `scl.resetauth` (no file edits needed).

The gate **never** shows for players already in the allow-list and for admins
when `BypassAdmins` is on — this is by design.

---

### 🧩 Compatible hooks (Oxide vs Carbon)

On **Carbon**, hooks that patch game methods (whose signatures vary per Rust
build) are excluded at compile time to avoid `Invalid IL code` / `Signature not
found` errors. The gate still fully freezes the player via input lock +
snap-back + blocked chat/commands/voice + frozen metabolism. On **Oxide** the
full hook set is active.

---

## 🛠 Usage

Manage the lock from the **server console**:

| Command | Description |
|---|---|
| `scl.setpass <digits>` | Set/change the PIN (digits only, max 8) |
| `scl.grant <steamid>` | Add a player to the allow-list |
| `scl.revoke <steamid>` | Remove a player from the allow-list |
| `scl.resetauth` | Clear the entire allow-list |
| `scl.status` | Show current state |

Players type the PIN on the in-game keypad (`1-9`, `C` = clear, `↵` = enter).

---

## ⚙️ Configuration (`ServerCodeLock.json`)

| Option | Default | Description |
|---|---|---|
| `Password` | `1234` | Server PIN |
| `MaxAttemptsBeforeKick` | `3` | Kick after N wrong attempts |
| `MaxAttemptsBeforeBan` | `10` | Ban after N wrong attempts |
| `KickReason` | *default* | Kick message |
| `BanReason` | *default* | Ban message |
| `GateTimeoutSeconds` | `180` | Auto-kick when idle (`0` = off) |
| `GateTimeoutReason` | *default* | Timeout kick message |
| `BypassAdmins` | `true` | Admins skip the gate |
| `BypassModerators` | `false` | Moderators skip the gate |
| `BroadcastUnlock` | `false` | Announce successful entry |
| `UseBlur` | `true` | Blurred background |
| `Language` | `en` | `en` or `ru` |
| `LogToFile` | `true` | Write logs to `oxide/logs/ServerCodeLock/` |
| `DebugVerbose` | `false` | Verbose debug logging |

### Permissions

| Permission | Effect |
|---|---|
| `servercodelock.bypass` | Bypass the gate |
| `servercodelock.admin` | Use admin commands (`grant`, `revoke`, `setpass`, …) |

---

## ❓ FAQ

**Players get stuck or are kicked on join.**
The gate is active until the PIN is entered. Enable `BypassAdmins` or add the
player with `scl.grant <steamid>`.

**Entered the wrong PIN / forgot it?**
Run `scl.setpass <digits>` or change `Password` in the config.

**Where are the logs?**
`oxide/logs/ServerCodeLock/` (when `LogToFile` is `true`).

**How do I switch the language?**
Set `Language` to `ru` or `en` in the config.

**Does it work with Carbon?**
Yes — the plugin uses the Oxide API that Carbon emulates.

---

## 📄 License

Released under the [MIT License](LICENSE) — **do whatever you want** with the
code: use, modify, redistribute, sell, fork. Attribution appreciated but not
required.

---

## ☕ Support

If this plugin saved your server and you want to say thanks or buy a coffee,
**subscribe to my Telegram channel:** [@sqd728](https://t.me/sqd728)

---

## 🇷🇺 Русский

> Разовый PIN-шлюз для приватных серверов Rust.

**Автор:** [tzuxi728](https://github.com/tzuxi728) · **Telegram:** [@tzuxi](https://t.me/tzuxi)

Лёгкий серверный замок: при входе каждый игрок, которого нет в списке допуска,
должен ввести числовой PIN. Работает и на **Oxide**, и на **Carbon**. Идеален
для приватных/закрытых серверов.

### ✨ Возможности

- 🔒 Экран ввода PIN при входе (и после сна/респавна)
- 🧹 **Сброс на каждом вайпе** — список допуска очищается при новом сейве
- 👥 Список допуска (`grant` / `revoke`) с сохранением
- 🚨 Лимит попыток → кик, затем бан
- ⏱ Автокик по таймауту бездействия
- 🛡 Пауза античита и возврат на место, пока открыт шлюз
- 🧊 Блокирует урон, прицеливание, лут, строительство, чат и другое
- 🌐 Двуязычный интерфейс (рус / англ)
- 📝 Логи в файл и режим отладки

### 📥 Установка

**1. Скопируйте файл плагина**

- **Oxide:** положите `ServerCodeLock.cs` в `oxide/plugins/`
- **Carbon:** положите `ServerCodeLock.cs` в `carbon/plugins/`

**2. Загрузите / перезагрузите**

```
oxide.reload ServerCodeLock         # Oxide
carbon.plugin reload ServerCodeLock # Carbon
```

При обновлении уже загруженного плагина сделайте чистую перезагрузку, чтобы сервер
перекомпилил новый исходник:

```
oxide.plugin unload ServerCodeLock && oxide.plugin load ServerCodeLock   # Oxide
carbon.plugin unload ServerCodeLock && carbon.plugin load ServerCodeLock # Carbon
```

**3. Задайте PIN** (иначе используется дефолт `1234` и печатается предупреждение):

```
scl.setpass 4829
```

**4. Готово.** Любой игрок, которого **нет** в списке допуска, видит клавиатуру
при входе и должен ввести PIN. Админы (конфиг `BypassAdmins`) по умолчанию
пропускают гейт.

---

### 💾 Данные и сброс (важно)

Список допуска хранится в `ServerCodeLock/state.json` (поля `WipeId`,
`Authorized`, `Attempts`). Путь зависит от фреймворка:

- **Oxide:** `oxide/data/ServerCodeLock/state.json`
- **Carbon:** `carbon/data/ServerCodeLock/state.json`

> ⚠️ Удаление только legacy-файла `authorized` **не** очищает список — он лежит
> внутри `state.json`. Для полного сброса удалите всю папку `ServerCodeLock`
> целиком **или** используйте `scl.resetauth` (без правки файлов).

Гейт **никогда** не показывается игрокам из списка допуска и админам при
`BypassAdmins` — это задумано.

---

### 🧩 Хуки на Oxide vs Carbon

На **Carbon** хуки, перехватывающие игровые методы (чьи сигнатуры меняются от
сборки к сборке Rust), отключаются на этапе компиляции — чтобы не было ошибок
`Invalid IL code` / `Signature not found`. Гейт при этом по-прежнему полностью
замораживает игрока: блокировка ввода, возврат на место, блокировка
чата/команд/войса, замороженный метаболизм. На **Oxide** активен полный набор.

### 🛠 Команды

| Команда | Описание |
|---|---|
| `scl.setpass <цифры>` | Сменить PIN (только цифры, до 8) |
| `scl.grant <steamid>` | Добавить игрока в допуск |
| `scl.revoke <steamid>` | Убрать игрока из допуска |
| `scl.resetauth` | Очистить весь список допуска |
| `scl.status` | Текущее состояние |

Игроки вводят PIN на клавиатуре (`1-9`, `C` = сброс, `↵` = ввод).

### ⚙️ Конфиг (`ServerCodeLock.json`)

| Параметр | По умолчанию | Описание |
|---|---|---|
| `Password` | `1234` | PIN сервера |
| `MaxAttemptsBeforeKick` | `3` | Кик после N неверных попыток |
| `MaxAttemptsBeforeBan` | `10` | Бан после N неверных попыток |
| `KickReason` | *по умолч.* | Сообщение кика |
| `BanReason` | *по умолч.* | Сообщение бана |
| `GateTimeoutSeconds` | `180` | Автокик при бездействии (`0` = выкл) |
| `GateTimeoutReason` | *по умолч.* | Сообщение таймаута |
| `BypassAdmins` | `true` | Админы пропускают шлюз |
| `BypassModerators` | `false` | Модераторы пропускают шлюз |
| `BroadcastUnlock` | `false` | Оповещать о входе |
| `UseBlur` | `true` | Размытие фона |
| `Language` | `en` | `en` или `ru` |
| `LogToFile` | `true` | Логи в `oxide/logs/ServerCodeLock/` |
| `DebugVerbose` | `false` | Подробный лог |

### Права

| Право | Действие |
|---|---|
| `servercodelock.bypass` | Пропускает шлюз |
| `servercodelock.admin` | Админ-команды (`grant`, `revoke`, `setpass`, …) |

### ❓ FAQ

**Игроков кикает или они зависают при входе.**
Шлюз активен, пока не введён PIN. Включите `BypassAdmins` или добавьте игрока
через `scl.grant <steamid>`.

**Ввёл неверный PIN / забыл его?**
Выполните `scl.setpass <цифры>` или поменяйте `Password` в конфиге.

**Где логи?**
`oxide/logs/ServerCodeLock/` (при `LogToFile = true`).

**Как сменить язык?**
Поставьте `Language` в `ru` или `en` в конфиге.

**Работает с Carbon?**
Да — плагин использует Oxide API, который эмулирует Carbon.

### 📄 Лицензия

Выпущено под [MIT License](LICENSE) — **делайте с кодом что хотите**: используйте,
изменяйте, распространяйте, продавайте, форкайте. Указание автора приветствуется,
но не обязательно.

### ☕ Поддержка

Если плагин спас ваш сервер и вы хотите отблагодарить — **подпишитесь на мой
Telegram-канал:** [@sqd728](https://t.me/sqd728)
