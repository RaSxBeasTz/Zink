# Zink free Cloudflare backend

This Worker replaces the charged Google Cloud host for authentication, friends,
RTC configuration, and WebSocket call signalling. It uses only Workers Free,
D1 Free, and SQLite Durable Objects Free. Do not upgrade the Cloudflare account
to a paid Workers plan.

Media uses peer-to-peer WebRTC. The backend does not provide TURN relay service;
on restrictive networks a call may fail instead of incurring a relay bill.

Friend presence is derived from authenticated WebSocket connections held by the
global signaling Durable Object. The desktop Friends page refreshes this status
while it is visible.
