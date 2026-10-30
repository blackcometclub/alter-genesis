<script lang="ts">
  type CardSize = "small" | "large";

  type Notification = {
    id: string;
    start_time: string;
    title: string;
    calendar_color: string;
    card_size: CardSize;
  };

  type Props = {
    notification: Notification;
    closeLabel: string;
    busy: boolean;
    onDismiss: () => void | Promise<void>;
  };

  let { notification, closeLabel, busy, onDismiss }: Props = $props();
</script>

<article
  class:large={notification.card_size === "large"}
  class="notification-card"
  aria-label={notification.title}
  aria-busy={busy}
>
  <div class="calendar-band" style:background={notification.calendar_color} aria-hidden="true"></div>

  <div class="content">
    <div class="heading">
      <span class="start-time">{notification.start_time}</span>
      <span class="event-title">{notification.title}</span>
    </div>

    <div class="actions">
      <button type="button" disabled={busy} onclick={onDismiss}>{closeLabel}</button>
    </div>
  </div>
</article>

<style>
  .notification-card {
    --control-height: 32px;
    display: grid;
    grid-template-columns: 40px minmax(0, 1fr);
    width: 100vw;
    height: 100vh;
    overflow: hidden;
    border: 1px solid #4b494d;
    border-radius: 14px;
    background: #303033;
    color: #dbdbdb;
    box-shadow: 0 14px 32px rgb(0 0 0 / 34%);
  }

  .calendar-band {
    width: 40px;
    height: 100%;
  }

  .content {
    display: grid;
    grid-template-rows: 56px 8px 32px;
    min-width: 0;
    padding: 16px;
  }

  .notification-card.large .content {
    grid-template-rows: 68px 12px 32px;
    padding: 20px 24px;
  }

  .heading {
    display: grid;
    grid-template-columns: 84px 12px minmax(0, 1fr);
    align-items: center;
    min-width: 0;
  }

  .notification-card.large .heading {
    grid-template-columns: 84px 16px minmax(0, 1fr);
  }

  .start-time {
    grid-column: 1;
    font-family: "Segoe UI Variable Display", "Segoe UI", sans-serif;
    font-size: 28px;
    font-weight: 600;
    font-variant-numeric: lining-nums tabular-nums;
    line-height: 34px;
    white-space: nowrap;
  }

  .event-title {
    grid-column: 3;
    min-width: 0;
    overflow: hidden;
    font-size: 26px;
    font-weight: 500;
    line-height: 34px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .actions {
    grid-row: 3;
    display: flex;
    justify-content: flex-end;
    align-items: center;
  }

  button {
    min-width: 72px;
    height: var(--control-height);
    margin: 0;
    padding: 0 16px;
    border: 1px solid #53697f;
    border-radius: 6px;
    background: #465a70;
    color: #dbdbdb;
    font: inherit;
    font-size: 13px;
    font-weight: 600;
  }

  button:hover:not(:disabled) {
    border-color: #69839d;
    background: #4b607b;
  }

  button:active:not(:disabled) {
    background: #3c4e64;
  }

  button:focus-visible {
    outline: 2px solid #bcd0ea;
    outline-offset: 2px;
  }

  button:disabled {
    opacity: 0.62;
  }
</style>
