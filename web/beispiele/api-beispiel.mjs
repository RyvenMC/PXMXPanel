// Beispiel: RyvenPanel-API mit einem API-Schlüssel (Node.js 18+, ohne Pakete).
//   RYVENPANEL_KEY=rpk_... node api-beispiel.mjs
// Der Schlüssel funktioniert nur von den IPs, die beim Anlegen eingetragen wurden.

const BASIS = process.env.RYVENPANEL_URL ?? 'https://secure.ryvenmc.de';
const SCHLUESSEL = process.env.RYVENPANEL_KEY;
if (!SCHLUESSEL) throw new Error('RYVENPANEL_KEY fehlt');

async function aufruf(methode, pfad, daten) {
  const r = await fetch(BASIS + pfad, {
    method: methode,
    headers: { Authorization: 'Bearer ' + SCHLUESSEL, 'Content-Type': 'application/json' },
    body: daten === undefined ? undefined : JSON.stringify(daten),
  });
  const j = await r.json();
  if (j.status !== 'ok') throw new Error(`${j.error}: ${j.error_data}`);
  return j.data;
}

const mod = (cmd, args = {}) => aufruf('POST', '/panel/mod', { cmd, args });
const eco = (cmd, args = {}) => aufruf('POST', '/panel/eco', { cmd, args });

const ich = await aufruf('GET', '/panel/me');
console.log('Angemeldet als', ich.username, '– Rechte:', ich.perms.join(', '));

if (ich.perms.includes('servers.view'))
  for (const s of await aufruf('GET', '/api/v2/servers')) console.log('Server:', s.server_name, s.server_id);

if (ich.perms.includes('datenschutz.view')) {
  const st = await mod('ds_status');
  console.log(`Datenschutz: ${st.unbekannt} unbekannte Ziele, ${st.undokumentiert} Plugins ohne Doku`);
}

if (ich.perms.includes('economy.view')) {
  const w = await eco('summary', { days: 7 });
  console.log(`Geldmenge ${Math.round(w.supply_now)} $`);
}
