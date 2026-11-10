<script lang="ts">
  import { browser } from "$app/environment";
  import NotificationCard from "$lib/NotificationCard.svelte";
  import { invoke } from "@tauri-apps/api/core";
  import { getCurrentWindow } from "@tauri-apps/api/window";
  import { onMount } from "svelte";

  type Language = "system" | "english" | "japanese";
  type CardSize = "small" | "large";
  type NotificationSound = "none" | "koron" | "kirari" | "pikon";
  type SoundRepeat = "none" | "one_minute" | "three_minutes" | "five_minutes" | "ten_minutes";
  type Locale = "ja" | "en";

  type AppSettings = {
    language: Language;
    card_size: CardSize;
    notification_sound: NotificationSound;
    sound_repeat: SoundRepeat;
  };

  type ActiveNotification = {
    id: string;
    start_time: string;
    title: string;
    calendar_color: string;
    card_size: CardSize;
  };

  const defaults: AppSettings = {
    language: "system",
    card_size: "small",
    notification_sound: "koron",
    sound_repeat: "none",
  };

  const copy = {
    ja: {
      title: "設定 — ALTER GENESIS",
      language: "言語",
      cardSize: "カードの大きさ",
      notificationSound: "通知音",
      preview: "試聴",
      soundRepeat: "音の繰り返し",
      close: "閉じる",
      snooze: "5分後に再通知",
      notificationSnoozeError: "再通知を予約できませんでした。",
      notificationLoadError: "通知を表示できませんでした。",
      notificationCloseError: "通知を閉じられませんでした。",
      languageOptions: { system: "システム", english: "英語", japanese: "日本語" },
      cardSizeOptions: { small: "小さめ", large: "大きめ" },
      soundOptions: { none: "なし", koron: "ころん", kirari: "きらり", pikon: "ぴこん" },
      repeatOptions: {
        none: "繰り返さない",
        one_minute: "1分ごと",
        three_minutes: "3分ごと",
        five_minutes: "5分ごと",
        ten_minutes: "10分ごと",
      },
      errors: {
        settings_io_error: "設定を読み書きできませんでした。",
        settings_malformed: "設定ファイルを読み取れません。元の内容は変更していません。",
        settings_unsupported_version: "新しい形式の設定です。この版では変更できません。",
        settings_protected: "設定を保護しています。この版では変更できません。",
        settings_state_unavailable: "設定を一時的に利用できません。",
        notification_io_error: "通知を読み書きできませんでした。",
        notification_protected: "通知ファイルを読み取れません。元の内容は変更していません。",
        notification_in_use: "同じ保存先のALTER GENESISがすでに起動しています。",
        sound_playback_failed: "音を再生できませんでした。",
        sound_state_unavailable: "音を一時的に利用できません。",
        unknown: "設定を保存できませんでした。",
      },
    },
    en: {
      title: "Settings — ALTER GENESIS",
      language: "Language",
      cardSize: "Card size",
      notificationSound: "Notification sound",
      preview: "Preview",
      soundRepeat: "Repeat sound",
      close: "Close",
      snooze: "Remind in 5 min",
      notificationSnoozeError: "The reminder could not be scheduled.",
      notificationLoadError: "The notification could not be displayed.",
      notificationCloseError: "The notification could not be closed.",
      languageOptions: { system: "System", english: "English", japanese: "Japanese" },
      cardSizeOptions: { small: "Small", large: "Large" },
      soundOptions: { none: "None", koron: "Koron", kirari: "Kirari", pikon: "Pikon" },
      repeatOptions: {
        none: "Do not repeat",
        one_minute: "Every minute",
        three_minutes: "Every 3 minutes",
        five_minutes: "Every 5 minutes",
        ten_minutes: "Every 10 minutes",
      },
      errors: {
        settings_io_error: "Settings could not be read or written.",
        settings_malformed: "The settings file could not be read. Its contents were not changed.",
        settings_unsupported_version: "These settings use a newer format and cannot be changed here.",
        settings_protected: "The settings are protected and cannot be changed here.",
        settings_state_unavailable: "Settings are temporarily unavailable.",
        notification_io_error: "Notifications could not be read or written.",
        notification_protected: "The notification file could not be read. Its contents were not changed.",
        notification_in_use: "ALTER GENESIS is already using this data folder.",
        sound_playback_failed: "The sound could not be played.",
        sound_state_unavailable: "Sound is temporarily unavailable.",
        unknown: "Settings could not be saved.",
      },
    },
  } as const;

  const panelMode =
    browser && "__TAURI_INTERNALS__" in window && getCurrentWindow().label === "notification-panel";

  let settings = $state<AppSettings>({ ...defaults });
  let systemLocale = $state<Locale>("ja");
  let loading = $state(true);
  let saving = $state(false);
  let blocked = $state(false);
  let errorCode = $state("");
  let previewing = $state(false);
  let notification = $state<ActiveNotification | null>(null);
  let notificationBusy = $state(false);
  let notificationError = $state("");

  const locale = $derived<Locale>(
    settings.language === "japanese"
      ? "ja"
      : settings.language === "english"
        ? "en"
        : systemLocale,
  );
  const text = $derived(copy[locale]);
  const controlsDisabled = $derived(loading || saving || blocked);
  const errorMessage = $derived(
    errorCode
      ? text.errors[errorCode as keyof typeof text.errors] ?? text.errors.unknown
      : "",
  );

  onMount(async () => {
    systemLocale = navigator.language.toLowerCase().startsWith("ja") ? "ja" : "en";

    try {
      settings = await invoke<AppSettings>("load_settings");
      await applyLanguage();

      const savedNotification = await invoke<ActiveNotification | null>("load_notification");
      if (panelMode) {
        notification = savedNotification;
        if (!notification) {
          await getCurrentWindow().close();
        }
      }
    } catch (error) {
      if (panelMode) {
        notificationError = text.notificationLoadError;
      } else {
        blocked = true;
        errorCode = normalizeError(error);
      }
    } finally {
      loading = false;
      await applyLanguage();
    }
  });

  async function changeSetting<Key extends keyof AppSettings>(
    key: Key,
    value: AppSettings[Key],
  ) {
    const previous = { ...settings };
    settings = { ...settings, [key]: value };
    errorCode = "";
    saving = true;
    await applyLanguage();

    try {
      settings = await invoke<AppSettings>("save_settings", { settings });
    } catch (error) {
      settings = previous;
      errorCode = normalizeError(error);
      await applyLanguage();
    } finally {
      saving = false;
    }
  }

  async function snoozeNotification() {
    if (!notification || notificationBusy) return;
    notificationBusy = true;
    notificationError = "";
    try {
      await invoke("snooze_notification", { id: notification.id });
    } catch {
      notificationError = text.notificationSnoozeError;
    } finally {
      notificationBusy = false;
    }
  }

  async function previewSound() {
    if (controlsDisabled || previewing || settings.notification_sound === "none") return;
    previewing = true;
    errorCode = "";
    try {
      await invoke("preview_sound");
    } catch (error) {
      errorCode = normalizeError(error);
    } finally {
      previewing = false;
    }
  }

  async function dismissNotification() {
    if (!notification || notificationBusy) {
      return;
    }

    notificationBusy = true;
    notificationError = "";

    try {
      await invoke("dismiss_notification", { id: notification.id });
      await getCurrentWindow().close();
    } catch {
      notificationError = text.notificationCloseError;
      notificationBusy = false;
    }
  }

  async function applyLanguage() {
    const activeLocale: Locale =
      settings.language === "japanese"
        ? "ja"
        : settings.language === "english"
          ? "en"
          : systemLocale;
    const activeCopy = copy[activeLocale];
    const activeTitle = panelMode ? "ALTER GENESIS" : activeCopy.title;
    document.documentElement.lang = activeLocale;
    document.title = activeTitle;

    try {
      await getCurrentWindow().setTitle(activeTitle);
    } catch {
      // Browser preview does not provide a Tauri window.
    }
  }

  function normalizeError(error: unknown) {
    return typeof error === "string" ? error : "unknown";
  }
</script>

<svelte:head>
  <meta name="color-scheme" content="dark" />
</svelte:head>

{#if panelMode}
  <main class="notification-page" aria-busy={loading || notificationBusy}>
    {#if notification}
      <NotificationCard
        {notification}
        closeLabel={text.close}
        snoozeLabel={text.snooze}
        onSnooze={snoozeNotification}
        busy={notificationBusy}
        errorMessage={notificationError}
        onDismiss={dismissNotification}
      />
    {:else if notificationError}
      <div class="notification-error" role="alert">
        <span>{notificationError}</span>
        <button type="button" onclick={() => getCurrentWindow().close()}>{text.close}</button>
      </div>
    {/if}
  </main>
{:else}
  <main class="settings-page" aria-busy={loading || saving}>
    <div class="settings">
      <label>
        <span>{text.language}</span>
        <select
          value={settings.language}
          disabled={controlsDisabled}
          onchange={(event) =>
            changeSetting("language", event.currentTarget.value as Language)}
        >
          <option value="system">{text.languageOptions.system}</option>
          <option value="english">{text.languageOptions.english}</option>
          <option value="japanese">{text.languageOptions.japanese}</option>
        </select>
      </label>

      <label>
        <span>{text.cardSize}</span>
        <select
          value={settings.card_size}
          disabled={controlsDisabled}
          onchange={(event) =>
            changeSetting("card_size", event.currentTarget.value as CardSize)}
        >
          <option value="small">{text.cardSizeOptions.small}</option>
          <option value="large">{text.cardSizeOptions.large}</option>
        </select>
      </label>

      <div class="sound-setting">
        <label for="notification-sound">{text.notificationSound}</label>
        <div class="sound-controls">
          <select
            id="notification-sound"
            value={settings.notification_sound}
            disabled={controlsDisabled}
            onchange={(event) =>
              changeSetting("notification_sound", event.currentTarget.value as NotificationSound)}
          >
            <option value="none">{text.soundOptions.none}</option>
            <option value="koron">{text.soundOptions.koron}</option>
            <option value="kirari">{text.soundOptions.kirari}</option>
            <option value="pikon">{text.soundOptions.pikon}</option>
          </select>
          <button type="button" disabled={controlsDisabled || previewing || settings.notification_sound === "none"} onclick={previewSound}>{text.preview}</button>
        </div>
      </div>

      <label>
        <span>{text.soundRepeat}</span>
        <select
          value={settings.sound_repeat}
          disabled={controlsDisabled}
          onchange={(event) =>
            changeSetting("sound_repeat", event.currentTarget.value as SoundRepeat)}
        >
          <option value="none">{text.repeatOptions.none}</option>
          <option value="one_minute">{text.repeatOptions.one_minute}</option>
          <option value="three_minutes">{text.repeatOptions.three_minutes}</option>
          <option value="five_minutes">{text.repeatOptions.five_minutes}</option>
          <option value="ten_minutes">{text.repeatOptions.ten_minutes}</option>
        </select>
      </label>
    </div>

    {#if errorMessage}
      <p class="settings-error" role="alert">{errorMessage}</p>
    {/if}
  </main>
{/if}

<style>
  :global(*) {
    box-sizing: border-box;
  }

  :global(html),
  :global(body) {
    min-width: 320px;
    min-height: 100%;
    margin: 0;
    overflow: hidden;
    background: #2d2d30;
    color: #dbdbdb;
    font-family: "Segoe UI Variable Text", "Yu Gothic UI", "Segoe UI", sans-serif;
  }

  .settings-page {
    --control-height: 32px;
    min-height: 100vh;
    padding: 24px;
    background: #2d2d30;
  }

  .settings {
    display: grid;
    gap: 16px;
  }

  label {
    display: grid;
    gap: 8px;
    font-size: 14px;
    font-weight: 600;
    line-height: 20px;
  }

  .sound-setting {
    display: grid;
    gap: 8px;
  }

  .sound-controls {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 96px;
    gap: 8px;
  }

  .sound-controls button {
    height: var(--control-height);
    margin: 0;
    padding: 0 16px;
    border: 1px solid #53697f;
    border-radius: 6px;
    background: #465a70;
    color: #dbdbdb;
    font: inherit;
    font-size: 14px;
    cursor: pointer;
  }

  .sound-controls button:hover:not(:disabled) { background: #526d86; }
  .sound-controls button:active:not(:disabled) { background: #3b5065; }
  .sound-controls button:focus-visible {
    outline: 2px solid #8aa8c6;
    outline-offset: 1px;
  }
  .sound-controls button:disabled { opacity: 0.58; cursor: default; }

  select {
    width: 100%;
    height: var(--control-height);
    margin: 0;
    padding: 0 32px 0 12px;
    border: 1px solid #7a7a80;
    border-radius: 6px;
    background: #35353a;
    color: #dbdbdb;
    font: inherit;
    font-weight: 400;
  }

  select:hover:not(:disabled) {
    border-color: #a0a0a6;
  }

  select:focus-visible {
    border-color: #8aa8c6;
    outline: 2px solid #486984;
    outline-offset: 1px;
  }

  select:disabled {
    opacity: 0.58;
  }

  .settings-error {
    margin: 16px 0 0;
    color: #e6a5a5;
    font-size: 13px;
    line-height: 20px;
  }

  .notification-page {
    width: 100vw;
    height: 100vh;
    margin: 0;
    padding: 0;
  }

  .notification-error {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    width: 100vw;
    height: 100vh;
    padding: 16px;
    border: 1px solid #744b50;
    border-radius: 14px;
    background: #303033;
    color: #e6a5a5;
    font-size: 13px;
  }

  .notification-error button {
    min-width: 72px;
    height: 32px;
    border: 1px solid #53697f;
    border-radius: 6px;
    background: #465a70;
    color: #dbdbdb;
    font: inherit;
  }
</style>
