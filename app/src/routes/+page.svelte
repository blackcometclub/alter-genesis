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
  type GoogleCalendar = { id: string; name: string; color: string; primary: boolean };
  type GoogleView = { configured: boolean; connected: boolean; calendars: GoogleCalendar[]; selected_ids: string[]; error: string | null };

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
      google: "Googleカレンダー",
      connect: "接続",
      reconnect: "再接続",
      connecting: "接続中…",
      refresh: "更新",
      disconnect: "切断",
      cancel: "キャンセル",
      noCalendars: "カレンダーがありません。",
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
        google_not_configured: "Google接続を利用できません。",
        google_store_error: "接続情報を読み書きできませんでした。",
        google_store_protected: "接続情報を読み取れません。元の内容は変更していません。",
        google_client_changed: "接続情報を引き継げません。切断して、接続し直してください。",
        google_in_use: "同じ保存先のALTER GENESISがすでに起動しています。",
        google_busy: "Googleへの接続を処理しています。",
        google_state_unavailable: "Google接続を一時的に利用できません。",
        google_browser_failed: "ブラウザーを開けませんでした。接続し直してください。",
        google_callback_failed: "ログイン結果を受け取れませんでした。接続し直してください。",
        google_login_timeout: "ログインの待ち時間を過ぎました。接続し直してください。",
        google_reconnect_required: "Googleへの再接続が必要です。",
        google_auth_failed: "Googleにログインできませんでした。接続し直してください。",
        google_network_error: "Googleから取得できませんでした。通信を確認して、更新してください。",
        google_timeout: "Googleとの通信が間に合いませんでした。もう一度お試しください。",
        google_permission_denied: "カレンダー一覧の参照が許可されていません。再接続してください。",
        google_not_connected: "Googleに接続してください。",
        google_selection_invalid: "選択を保存できませんでした。更新して選び直してください。",
        google_catalog_invalid: "カレンダー一覧を読み取れませんでした。前の一覧を残しています。",
        unknown: "設定を保存できませんでした。",
      },
    },
    en: {
      title: "Settings — ALTER GENESIS",
      language: "Language",
      cardSize: "Card size",
      notificationSound: "Notification sound",
      preview: "Preview",
      google: "Google Calendar",
      connect: "Connect",
      reconnect: "Reconnect",
      connecting: "Connecting…",
      refresh: "Refresh",
      disconnect: "Disconnect",
      cancel: "Cancel",
      noCalendars: "No calendars.",
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
        google_not_configured: "Google connection is unavailable.",
        google_store_error: "Connection data could not be read or saved.",
        google_store_protected: "Connection data could not be read. Its contents were not changed.",
        google_client_changed: "This connection cannot be restored. Disconnect, then connect again.",
        google_in_use: "ALTER GENESIS is already using this data folder.",
        google_busy: "A Google connection is in progress.",
        google_state_unavailable: "Google connection is temporarily unavailable.",
        google_browser_failed: "The browser could not be opened. Connect again.",
        google_callback_failed: "The sign-in response was not received. Connect again.",
        google_login_timeout: "Sign-in timed out. Connect again.",
        google_reconnect_required: "Please reconnect to Google.",
        google_auth_failed: "Google sign-in failed. Connect again.",
        google_network_error: "Calendars could not be retrieved. Check your connection and refresh.",
        google_timeout: "The Google request timed out. Please try again.",
        google_permission_denied: "Calendar list access was not granted. Please reconnect.",
        google_not_connected: "Please connect to Google.",
        google_selection_invalid: "The selection could not be saved. Refresh and choose again.",
        google_catalog_invalid: "The calendar list could not be read. The previous list was kept.",
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
  let google = $state<GoogleView>({ configured: false, connected: false, calendars: [], selected_ids: [], error: null });
  let googleBusy = $state(true);
  let selectionSaving = $state(false);
  let googleError = $state("");
  const needsReconnect = $derived(googleError === "google_reconnect_required" || googleError === "google_permission_denied");
  const googleAccount = $derived(google.calendars.find((calendar) => calendar.primary)?.id ?? "");
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
  const googleMessage = $derived(googleError ? text.errors[googleError as keyof typeof text.errors] ?? text.errors.google_network_error : "");
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
    if (!panelMode) {
      try {
        google = await invoke<GoogleView>("load_google");
        googleError = google.error ?? (google.configured ? "" : "google_not_configured");
      } catch (error) {
        googleError = normalizeError(error);
      } finally { googleBusy = false; }
    }
  });

  async function googleAction(command: "connect_google" | "refresh_google" | "disconnect_google") {
    if (googleBusy || selectionSaving) return;
    googleBusy = true;
    googleError = "";
    try {
      google = await invoke<GoogleView>(command);
      googleError = google.error ?? "";
    } catch (error) {
      googleError = normalizeError(error) === "google_cancelled" ? "" : normalizeError(error);
      try { google = await invoke<GoogleView>("load_google"); } catch { /* Keep the current list. */ }
    } finally { googleBusy = false; }
  }

  async function cancelGoogle() {
    try { await invoke("cancel_google"); } catch (error) { googleError = normalizeError(error); }
  }

  async function changeCalendar(event: Event, id: string) {
    const input = event.currentTarget as HTMLInputElement;
    const selected = input.checked;
    input.checked = google.selected_ids.includes(id);
    if (googleBusy || selectionSaving || !google.connected || google.error) return;
    selectionSaving = true;
    googleError = "";
    try {
      google = await invoke<GoogleView>("set_google_calendar_selected", { id, selected });
    } catch (error) {
      googleError = normalizeError(error);
    } finally {
      selectionSaving = false;
    }
  }

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
          <button class="settings-button" type="button" disabled={controlsDisabled || previewing || settings.notification_sound === "none"} onclick={previewSound}>{text.preview}</button>
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
    <section class="google-section" aria-label={text.google} aria-busy={googleBusy || selectionSaving}>
      <h2>{text.google}</h2>
      <div class="google-actions">
        {#if !google.connected || needsReconnect}
          <button class="settings-button" type="button" disabled={googleBusy || selectionSaving || !google.configured || !!google.error} onclick={() => googleAction("connect_google")}>{googleBusy ? text.connecting : needsReconnect ? text.reconnect : text.connect}</button>
        {/if}
        {#if google.connected}
          <button class="settings-button" type="button" disabled={googleBusy || selectionSaving} onclick={() => googleAction("refresh_google")}>{text.refresh}</button>
        {/if}
        {#if googleBusy}
          <button class="settings-button" type="button" onclick={cancelGoogle}>{text.cancel}</button>
        {:else if google.connected || google.error}
          <button class="settings-button" type="button" disabled={selectionSaving} onclick={() => googleAction("disconnect_google")}>{text.disconnect}</button>
        {/if}
      </div>
      {#if googleAccount}<p class="google-account">{googleAccount}</p>{/if}
      {#if google.calendars.length}
        <ul class="google-calendars">
          {#each google.calendars as calendar (calendar.id)}
            <li><label class="google-calendar-choice">
              <input type="checkbox" checked={google.selected_ids.includes(calendar.id)}
                disabled={googleBusy || !!google.error} aria-disabled={selectionSaving}
                onchange={(event) => changeCalendar(event, calendar.id)} />
              <span class="calendar-color" style:background-color={calendar.color} aria-hidden="true"></span>
              <span>{calendar.name}</span>
            </label></li>
          {/each}
        </ul>
      {:else if google.connected && !googleBusy && !googleError}
        <p class="google-message">{text.noCalendars}</p>
      {/if}
      {#if googleMessage}<p class="settings-error" role="alert">{googleMessage}</p>{/if}
    </section>
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
    height: 100vh;
    overflow-y: auto;
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

  .settings-button {
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

  .settings-button:hover:not(:disabled) { background: #526d86; }
  .settings-button:active:not(:disabled) { background: #3b5065; }
  .settings-button:focus-visible {
    outline: 2px solid #8aa8c6;
    outline-offset: 1px;
  }
  .settings-button:disabled { opacity: 0.58; cursor: default; }

  .google-section { margin-top: 24px; }
  .google-section h2 { margin: 0 0 8px; font-size: 14px; font-weight: 600; line-height: 20px; }
  .google-actions { display: flex; gap: 8px; }
  .google-account, .google-message { margin: 12px 0 0; font-size: 13px; line-height: 20px; overflow-wrap: anywhere; }
  .google-calendars { max-height: 160px; overflow-y: auto; margin: 12px 0 0; padding: 0; list-style: none; }
  .google-calendar-choice { display: flex; align-items: center; gap: 8px; min-height: var(--control-height); padding: 4px 0; font-size: 14px; font-weight: 400; line-height: 24px; overflow-wrap: anywhere; cursor: pointer; }
  .google-calendar-choice:has(input:disabled), .google-calendar-choice:has(input[aria-disabled="true"]) { cursor: default; opacity: 0.58; }
  .google-calendar-choice input { flex: 0 0 16px; width: 16px; height: 16px; margin: 0; accent-color: #8aa8c6; }
  .google-calendar-choice input:focus-visible { outline: 2px solid #8aa8c6; outline-offset: 2px; }
  .calendar-color { flex: 0 0 8px; width: 8px; height: 16px; border-radius: 2px; }

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
