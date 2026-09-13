# Security policy

Only the latest preview is supported. This project has no security response SLA.

Report suspected vulnerabilities privately through GitHub's **Report a vulnerability**
feature when available. If it is unavailable, open an issue requesting a private
contact channel without including exploit details, credentials, audio, or private
URLs. Do not post secrets in issues or logs.

The app captures audio only while the shortcut is held, up to ten minutes. A
configured provider receives that audio. Recovery/test recordings are retained
locally until manually deleted. Windows DPAPI protects saved keys at rest for the
current user; it is not a boundary against that user's processes. Logs omit normal
transcript content, but review diagnostics for sensitive provider error messages
before sharing. File ASR providers and gateway operators control their own storage.

Keep gateway services on loopback or an access-controlled private network. Configure
a bearer token and HTTPS/WSS when appropriate. Never expose the unauthenticated
reference gateway or local Parakeet adapter to the public internet.
