<script lang="ts">
  import { onMount } from "svelte";
  import { invoke } from "@tauri-apps/api/core";

  type ConnectionState = "idle" | "connecting" | "mock";

  const calendars = [
    { id: "important", name: "忘れたくない予定", color: "#ad4964", detail: "通院・締切・大切な約束" },
    { id: "personal", name: "プライベート", color: "#d88354", detail: "個人の予定" },
    { id: "work", name: "仕事", color: "#556b8d", detail: "会議・業務予定" },
  ];

  const setupSteps = [
    { step: 1, label: "Google接続" },
    { step: 2, label: "カレンダー" },
    { step: 3, label: "通知テスト" },
    { step: 4, label: "完了" },
  ];

  let isPanel = $state(false);
  let connectionState = $state<ConnectionState>("idle");
  let selectedCalendars = $state<string[]>(["important"]);
  let notificationOffset = $state("10");
  let panelTested = $state(false);
  let showInlinePanel = $state(false);

  const setupStep = $derived(
    connectionState !== "mock" ? 1 : selectedCalendars.length === 0 ? 2 : panelTested ? 4 : 3,
  );

  onMount(() => {
    isPanel = new URLSearchParams(window.location.search).get("panel") === "1";
  });

  function connectMock() {
    if (connectionState !== "idle") return;
    connectionState = "connecting";
    window.setTimeout(() => {
      connectionState = "mock";
    }, 550);
  }

  function toggleCalendar(id: string) {
    selectedCalendars = selectedCalendars.includes(id)
      ? selectedCalendars.filter((calendarId) => calendarId !== id)
      : [...selectedCalendars, id];
  }

  async function openTestPanel() {
    panelTested = true;
    try {
      await invoke("open_test_panel");
    } catch {
      showInlinePanel = true;
    }
  }

  async function closePanel() {
    try {
      const { getCurrentWindow } = await import("@tauri-apps/api/window");
      await getCurrentWindow().close();
    } catch {
      showInlinePanel = false;
    }
  }
</script>

<svelte:head>
  <title>{isPanel ? "刻限通知 - ALTER GENESIS" : "断罪の刻印祭壇 - ALTER GENESIS"}</title>
</svelte:head>

{#if isPanel}
  <main class="panel-page">
    <section class="notification-panel" aria-label="テスト通知">
      <div class="panel-sigil" aria-hidden="true"><span></span></div>
      <div class="panel-content">
        <div class="panel-meta"><span>刻限まで10分</span><span class="status-dot">テスト</span></div>
        <h1>歯医者の予約</h1>
        <p class="calendar-name">忘れたくない予定</p>
        <dl>
          <div><dt>開始</dt><dd>15:00</dd></div>
          <div><dt>メモ</dt><dd>診察券を持っていく</dd></div>
        </dl>
        <div class="panel-actions">
          <button class="quiet-button" type="button">再通知</button>
          <button class="primary-button" type="button" onclick={closePanel}>確認して閉じる</button>
        </div>
      </div>
    </section>
  </main>
{:else}
  <main class="app-shell">
    <aside class="sidebar">
      <div class="brand-mark" aria-hidden="true"><span></span></div>
      <div class="brand-copy">
        <p class="eyebrow">AUTOMATON PROTOCOL</p>
        <h1>ALTER<br />GENESIS</h1>
        <p class="jp-name">断罪の刻印祭壇</p>
      </div>

      <nav aria-label="初回設定の進行">
        {#each setupSteps as item}
          <div class:active={setupStep === item.step} class:done={setupStep > item.step} class="nav-step">
            <span class="step-number">{setupStep > item.step ? "✓" : item.step}</span><span>{item.label}</span>
          </div>
        {/each}
      </nav>
      <p class="sidebar-note">忘れたくない予定を、<br />消える通知にしない。</p>
    </aside>

    <section class="workspace">
      <header class="topbar">
        <div><p class="eyebrow">PROTOCOL 00</p><h2>初回設定</h2></div>
        <span class="mock-badge">隔離モック</span>
      </header>

      <div class="notice-strip"><span aria-hidden="true">i</span><p>現在はUI確認用です。Googleアカウントへは接続しません。</p></div>

      <section class="setup-section" aria-labelledby="connection-heading">
        <div class="section-heading"><span>01</span><div><h3 id="connection-heading">Googleカレンダー</h3><p>読み取り専用で接続します。</p></div></div>
        <div class="connection-row">
          <div class="google-symbol" aria-hidden="true">G</div>
          <div class="connection-copy">
            <strong>{connectionState === "mock" ? "モックカレンダー" : "Google Calendar"}</strong>
            <span>{connectionState === "mock" ? "隔離データで接続手順を確認中" : "まだ接続していません"}</span>
          </div>
          <button class:connected={connectionState === "mock"} class="connect-button" type="button" onclick={connectMock} disabled={connectionState !== "idle"}>
            {connectionState === "idle" ? "接続手順を試す" : connectionState === "connecting" ? "接続中…" : "モック接続済み"}
          </button>
        </div>
      </section>

      <section class:disabled={connectionState !== "mock"} class="setup-section" aria-labelledby="calendar-heading">
        <div class="section-heading"><span>02</span><div><h3 id="calendar-heading">強い通知の対象</h3><p>消えない通知を出すカレンダーを選びます。</p></div></div>
        <div class="calendar-list">
          {#each calendars as calendar}
            <label class="calendar-item">
              <input type="checkbox" checked={selectedCalendars.includes(calendar.id)} onchange={() => toggleCalendar(calendar.id)} disabled={connectionState !== "mock"} />
              <span class="color-dot" style:background={calendar.color}></span>
              <span class="calendar-copy"><strong>{calendar.name}</strong><small>{calendar.detail}</small></span>
              <span class="check-visual" aria-hidden="true">✓</span>
            </label>
          {/each}
        </div>
        <div class="timing-row">
          <label for="notification-offset">通知タイミング</label>
          <select id="notification-offset" bind:value={notificationOffset} disabled={connectionState !== "mock"}>
            <option value="0">開始時刻</option><option value="5">5分前</option><option value="10">10分前</option><option value="30">30分前</option><option value="60">1時間前</option>
          </select>
          <button class="test-button" type="button" onclick={openTestPanel} disabled={connectionState !== "mock" || selectedCalendars.length === 0}>テスト通知を開く</button>
        </div>
      </section>

      {#if panelTested}
        <div class="success-row" role="status"><span aria-hidden="true">✓</span><p><strong>通知テストを開きました。</strong> 自動では消えず、「確認して閉じる」で閉じます。</p></div>
      {/if}
      {#if showInlinePanel}
        <div class="inline-preview"><p>ブラウザー確認用プレビュー</p><button type="button" onclick={() => (showInlinePanel = false)}>閉じる</button></div>
      {/if}
    </section>
  </main>
{/if}

<style>
  :global(*) { box-sizing: border-box; }
  :global(:root) {
    font-family: "Segoe UI Variable", "Yu Gothic UI", "Meiryo", sans-serif;
    color: #292326; background: #f7f3f1; font-synthesis: none; text-rendering: optimizeLegibility;
    --control-height: 40px; --ink: #292326; --muted: #766a6f; --line: #ded4d6; --paper: #fffdfb;
    --wine: #7d3047; --wine-dark: #622236; --rose-soft: #f6ecef;
  }
  :global(body) { margin: 0; min-width: 320px; min-height: 100vh; background: #f7f3f1; }
  :global(button), :global(select) { height: var(--control-height); font: inherit; }
  :global(button) { cursor: pointer; }
  :global(button:focus-visible), :global(select:focus-visible), :global(input:focus-visible + .color-dot) { outline: 3px solid rgba(125, 48, 71, .25); outline-offset: 2px; }
  .app-shell { min-height: 100vh; display: grid; grid-template-columns: 244px minmax(0, 1fr); }
  .sidebar {
    position: relative; overflow: hidden; display: flex; flex-direction: column; min-height: 100vh; padding: 40px 32px 32px; color: #fff8f7;
    background: radial-gradient(circle at 18% 10%, rgba(229, 143, 163, .28), transparent 30%), linear-gradient(165deg, #47202c 0%, #24181d 58%, #171317 100%);
  }
  .sidebar::after { content: ""; position: absolute; right: -116px; bottom: 72px; width: 232px; height: 232px; border: 1px solid rgba(255,255,255,.08); border-radius: 50%; box-shadow: 0 0 0 28px rgba(255,255,255,.025), 0 0 0 56px rgba(255,255,255,.018); }
  .brand-mark, .panel-sigil { position: relative; display: grid; place-items: center; width: 40px; height: 40px; border: 1px solid currentColor; border-radius: 50%; }
  .brand-mark::before, .brand-mark::after, .panel-sigil::before, .panel-sigil::after { content: ""; position: absolute; background: currentColor; }
  .brand-mark::before, .panel-sigil::before { width: 54px; height: 1px; }
  .brand-mark::after, .panel-sigil::after { width: 1px; height: 54px; }
  .brand-mark span, .panel-sigil span { width: 7px; height: 7px; border-radius: 50%; background: currentColor; }
  .brand-copy { margin-top: 24px; } .eyebrow { margin: 0; font-size: 10px; font-weight: 700; letter-spacing: .2em; }
  .brand-copy h1 { margin: 8px 0 0; font-size: 29px; line-height: .92; letter-spacing: .04em; }
  .jp-name { margin: 12px 0 0; color: #d6b9c1; font-size: 12px; }
  nav { display: grid; gap: 4px; margin-top: 64px; }
  .nav-step { display: flex; align-items: center; gap: 12px; min-height: 40px; padding: 0 8px; color: #a9949a; border-radius: 6px; font-size: 13px; }
  .nav-step.active { color: #fff; background: rgba(255,255,255,.08); } .nav-step.done { color: #decbd0; }
  .step-number { display: grid; place-items: center; width: 24px; height: 24px; border: 1px solid rgba(255,255,255,.25); border-radius: 50%; font-size: 11px; }
  .nav-step.active .step-number { color: #371a23; background: #e7b5c2; border-color: #e7b5c2; }
  .nav-step.done .step-number { color: #fff; background: #7d3047; border-color: #a55a70; }
  .sidebar-note { z-index: 1; margin: auto 0 0; color: #bca8ae; font-size: 12px; line-height: 1.8; }
  .workspace { padding: 40px clamp(40px, 6vw, 72px) 48px; }
  .topbar { display: flex; align-items: flex-end; justify-content: space-between; }
  .topbar .eyebrow { color: var(--wine); } .topbar h2 { margin: 4px 0 0; font-size: 30px; font-weight: 720; letter-spacing: .01em; }
  .mock-badge { padding: 6px 10px; color: #6f4f59; background: #eadfe2; border-radius: 999px; font-size: 11px; font-weight: 700; letter-spacing: .08em; }
  .notice-strip { display: flex; align-items: center; gap: 12px; margin-top: 24px; padding: 12px 16px; color: #5c4d52; background: #eee7e4; border-radius: 6px; font-size: 12px; }
  .notice-strip span { display: grid; place-items: center; width: 20px; height: 20px; color: #fff; background: #8d777d; border-radius: 50%; font-weight: 700; }
  .notice-strip p { margin: 0; }
  .setup-section { margin-top: 32px; } .setup-section.disabled { opacity: .46; }
  .section-heading { display: flex; align-items: flex-start; gap: 16px; }
  .section-heading > span { padding-top: 4px; color: var(--wine); font: 700 11px/1 "Segoe UI", sans-serif; letter-spacing: .12em; }
  .section-heading h3 { margin: 0; font-size: 17px; } .section-heading p { margin: 4px 0 0; color: var(--muted); font-size: 12px; }
  .connection-row { display: flex; align-items: center; gap: 16px; min-height: 72px; margin-top: 16px; padding: 16px; background: var(--paper); border: 1px solid var(--line); border-radius: 10px; }
  .google-symbol { display: grid; place-items: center; width: 40px; height: 40px; flex: 0 0 40px; color: #3f5f93; background: #edf1f8; border-radius: 9px; font: 700 18px/1 "Segoe UI", sans-serif; }
  .connection-copy { display: grid; gap: 2px; min-width: 0; } .connection-copy strong { font-size: 13px; } .connection-copy span { color: var(--muted); font-size: 11px; }
  .connect-button, .test-button, .primary-button, .quiet-button { border-radius: 7px; padding: 0 16px; font-size: 12px; font-weight: 700; }
  .connect-button { margin-left: auto; color: #fff; background: var(--wine); border: 1px solid var(--wine); }
  .connect-button:hover:not(:disabled), .test-button:hover:not(:disabled), .primary-button:hover { background: var(--wine-dark); }
  .connect-button.connected { color: #42604d; background: #e0ece4; border-color: #c3d8ca; }
  button:disabled { cursor: default; opacity: .7; }
  .calendar-list { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px; margin-top: 16px; }
  .calendar-item { position: relative; display: grid; grid-template-columns: 12px minmax(0, 1fr) 20px; align-items: center; gap: 10px; min-height: 64px; padding: 12px; background: var(--paper); border: 1px solid var(--line); border-radius: 8px; cursor: pointer; }
  .calendar-item:has(input:checked) { background: var(--rose-soft); border-color: #c995a4; }
  .calendar-item input { position: absolute; width: 1px; height: 1px; opacity: 0; }
  .color-dot { width: 10px; height: 10px; border-radius: 50%; }
  .calendar-copy { display: grid; gap: 2px; min-width: 0; } .calendar-copy strong, .calendar-copy small { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .calendar-copy strong { font-size: 12px; } .calendar-copy small { color: var(--muted); font-size: 10px; }
  .check-visual { display: none; place-items: center; width: 20px; height: 20px; color: #fff; background: var(--wine); border-radius: 50%; font-size: 11px; }
  .calendar-item input:checked ~ .check-visual { display: grid; }
  .timing-row { display: grid; grid-template-columns: auto 180px 180px; align-items: center; gap: 12px; margin-top: 16px; }
  .timing-row label { font-size: 12px; font-weight: 700; } .timing-row select { width: 100%; padding: 0 36px 0 12px; color: var(--ink); background: var(--paper); border: 1px solid var(--line); border-radius: 7px; }
  .test-button { color: #fff; background: var(--wine); border: 1px solid var(--wine); }
  .success-row { display: flex; align-items: center; gap: 12px; margin-top: 24px; padding: 16px; color: #31513d; background: #e7f0e9; border: 1px solid #c8dbcc; border-radius: 8px; font-size: 12px; }
  .success-row > span { display: grid; place-items: center; width: 24px; height: 24px; flex: 0 0 24px; color: #fff; background: #4f775b; border-radius: 50%; } .success-row p { margin: 0; }
  .inline-preview { display: flex; align-items: center; justify-content: space-between; margin-top: 16px; padding: 12px 16px; background: #fff; border: 1px dashed #b9a5ab; border-radius: 8px; }
  .inline-preview p { margin: 0; color: var(--muted); font-size: 12px; } .inline-preview button { padding: 0 14px; color: var(--wine); background: #fff; border: 1px solid #caaab3; border-radius: 7px; }
  .panel-page { min-height: 100vh; display: grid; place-items: center; padding: 20px; background: linear-gradient(145deg, #2e1a21, #160f13); }
  .notification-panel { display: grid; grid-template-columns: 56px minmax(0, 1fr); gap: 16px; width: 100%; max-width: 380px; padding: 20px; color: #2c2427; background: #fffaf7; border: 1px solid #ad7383; border-radius: 14px; box-shadow: 0 20px 50px rgba(0,0,0,.34); }
  .panel-sigil { width: 44px; height: 44px; margin: 5px; color: var(--wine); } .panel-sigil::before { width: 56px; } .panel-sigil::after { height: 56px; }
  .panel-meta { display: flex; align-items: center; justify-content: space-between; color: var(--wine); font-size: 11px; font-weight: 700; letter-spacing: .06em; }
  .status-dot { padding: 4px 7px; color: #6b555c; background: #eee3e6; border-radius: 999px; font-size: 9px; }
  .panel-content h1 { margin: 8px 0 2px; font-size: 22px; line-height: 1.25; } .calendar-name { margin: 0; color: var(--muted); font-size: 11px; }
  .panel-content dl { display: grid; gap: 6px; margin: 16px 0 0; } .panel-content dl div { display: grid; grid-template-columns: 44px 1fr; gap: 8px; font-size: 11px; }
  .panel-content dt { color: var(--muted); } .panel-content dd { margin: 0; }
  .panel-actions { display: grid; grid-template-columns: 96px 1fr; gap: 8px; margin-top: 20px; }
  .quiet-button { color: var(--wine); background: transparent; border: 1px solid #c8a7b0; } .primary-button { color: #fff; background: var(--wine); border: 1px solid var(--wine); }
  @media (max-width: 940px) { .app-shell { grid-template-columns: 208px minmax(0,1fr); } .sidebar { padding-inline: 24px; } .workspace { padding-inline: 32px; } .calendar-list { grid-template-columns: 1fr; } }
  @media (prefers-reduced-motion: no-preference) { .connection-row, .calendar-item, button { transition: border-color 140ms ease, background 140ms ease, opacity 140ms ease; } }
</style>
