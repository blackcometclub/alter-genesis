# Google verification submission draft

Draft for review, 2026-11-11. Not submitted. Use only after the public policy and in-app policy access match the actual Tauri build.

## App purpose

ALTER GENESIS is a Windows desktop reminder app. Users connect their Google Account, choose calendars, and receive persistent reminder cards with optional sounds. For timed events, the app uses Google Calendar popup reminder settings, including event overrides and calendar defaults. All-day events follow the app's own rule of 9:00 a.m. on the event date in the PC's local time zone. Event creation and editing remain in Google Calendar.

## Scope: calendar.calendarlist.readonly

Full scope: `https://www.googleapis.com/auth/calendar.calendarlist.readonly`

The app retrieves the user's subscribed calendar list so the user can select which calendars should generate reminders. Calendar names and colors identify each calendar in Settings and on reminder cards. Calendar identifiers route subsequent event requests only to the calendars the user selects. The app does not create, subscribe to, edit, or remove calendars.

Free/busy information cannot provide the calendar selection list and its names and colors. The app uses the read-only calendar-list scope instead of a scope that permits changing the subscription list.

## Scope: calendar.events.readonly

Full scope: `https://www.googleapis.com/auth/calendar.events.readonly`

The app reads event titles, start times or dates, cancellation status, popup reminder settings, and calendar default reminders for the calendars the user selects. It uses this information to calculate reminder times and display local cards. It reads attendee self flags and response statuses to exclude events declined by the user; it does not request attendee names or email addresses. Data is not uploaded to a TINMOON server.

Free/busy scopes do not provide event titles or popup reminder settings. An owned-events-only scope would exclude events on shared calendars that the user can read but does not own. The app requests the read-only events scope and never calls event creation, update, or deletion endpoints.

## Data handling summary

Google authentication uses the user's external browser, authorization code flow with PKCE S256, and state validation. The app communicates directly with Google. Google connection data and the Google reminder queue are encrypted locally with Windows CurrentUser DPAPI. App settings and the separate local reminder store use their own files; do not claim all application files are encrypted.

Closing a Google reminder marks its local record dismissed to prevent duplicate notifications. A successful local Disconnect removes Google connection information and the Google reminder queue. It does not revoke the Google Account's app authorization. Users can manage that authorization in their Google Account and can remove local data separately. Public retention and deletion wording must describe these behaviors accurately before submission.

## Demonstration sequence

Use the final candidate build and synthetic event content. Do not use personal event details. Use English in the Google consent flow and in the app for the demonstration.

1. Show the app name and the public introduction/privacy pages. In the app, show the policy entry once it has been implemented and verified.
2. Click Connect in the desktop app. Show the external browser flow, the correct app name, both requested read-only permissions, and the OAuth client ID in the consent page address bar. The client ID is a public identifier; do not reveal the client secret, tokens, verification codes, or unrelated account information.
3. After the user grants consent, show the actual calendar list and select the test calendar. Explain that the list scope enables this selection.
4. In Google Calendar, prepare a short test event with a popup reminder a few minutes before its start. Show the start time and reminder offset.
5. Refresh the desktop app, then show the resulting card and optional sound at the computed reminder time. Explain that the event scope provides the title, time, and reminder settings. Do not substitute the app's sample-preview notification for this step.
6. Show Remind in 5 min and Close if useful to explain local use. Do not claim the app edits the Google event. Do not demonstrate a card-to-calendar link until it exists in the final build.
7. Close with the local storage and disconnect behavior. If demonstrating a real disconnection, use an agreed test connection rather than the user's daily connection.

Prepare a real recording and review it before upload. Upload and verification submission are separate actions requiring explicit authorization. The recording must remain accessible to Google's reviewers. A written script or screenshots alone are not a recorded demonstration.

## Fields still to fill

- Final candidate version and binary identity.
- Reviewed demonstration-video URL.
- Current Console contact information and review-specific questions.
- Explanation/evidence for any unresolved current Console diagnostics.

See [the preparation record](README.md) for the observed Console state, remaining changes, and official sources.
