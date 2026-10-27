import { useState, type FormEvent } from "react";
import { ApiError, login } from "../api";
import { saveStaff, type StaffSession } from "../auth";

export function StaffLogin({ notice, onLogin }: { notice: string | null; onLogin: (s: StaffSession) => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!username.trim() || !password) {
      setError("Kullanıcı adını ve parolayı girin.");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const r = await login(username.trim(), password);
      const session: StaffSession = { token: r.accessToken, role: r.role, expiresAt: Date.now() + r.expiresInSec * 1000 };
      saveStaff(session);
      setPassword("");
      onLogin(session);
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) setError("Kullanıcı adı ya da parola hatalı.");
      else if (e instanceof ApiError && e.status === 429) setError("Çok fazla deneme yaptınız. Bir dakika bekleyip tekrar deneyin.");
      else setError("Sunucuya ulaşamadık. API'nin çalıştığından emin olun.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-labelledby="login-title" className="max-w-sm">
      <h1 id="login-title" className="font-display text-5xl font-bold leading-[0.95]">Operasyon panosu</h1>
      <p className="mt-2 text-steel">Bu sayfa yalnızca operasyon ve güvenlik personeli içindir. Oturum 15 dakika sürer.</p>
      {notice && <p className="mt-4 border-l-4 border-signal bg-tag p-3 text-sm">{notice}</p>}
      <form onSubmit={submit} className="mt-6 grid gap-4" noValidate>
        <label className="grid gap-1 text-sm font-semibold">
          Kullanıcı adı
          <input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" maxLength={64}
            className="border-2 border-ink bg-tag px-3 py-2 text-lg" />
        </label>
        <label className="grid gap-1 text-sm font-semibold">
          Parola
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" maxLength={256}
            className="border-2 border-ink bg-tag px-3 py-2 text-lg" />
        </label>
        <button type="submit" disabled={busy} className="border-2 border-ink bg-signal px-4 py-3 text-lg font-semibold hover:bg-ink hover:text-signal disabled:opacity-60">
          {busy ? "Giriş yapılıyor…" : "Giriş yap"}
        </button>
      </form>
      {error && (
        <p role="alert" className="mt-4 border-l-4 border-alert bg-tag p-3 text-sm text-alert">
          {error}
        </p>
      )}
    </section>
  );
}
