#!/usr/bin/env node
// Builds src/Nmst.SaveCleaner.Core/Resources/names_en.json: the small subset of the game's English text the app shows
// (quest titles, objective texts, district/location names), keyed the way save metadata references them.
//
// Inputs are WolvenKit CLI JSON exports from the game install:
//   onscreens: base\localization\en-us\onscreens\onscreens.json from content\lang_en_text.archive and ep1\lang_en_text.archive
//   journal:   base\journal\cooked_journal.journal from content\basegame_4_gamedata.archive and ep1\ep1_2_gamedata.archive
//              (same file name in both: convert them into separate folders, or one export overwrites the other)
// (extract with `WolvenKit.CLI extract <archive> -o <dir> -r <regex>`, then `WolvenKit.CLI convert s <file> -o <dir>`)
//
// Usage: node build-names.js <out.json> <export.json>...   (journal and onscreens exports, any order; base + ep1)
'use strict';
const fs = require('fs');

const [out, ...inputs] = process.argv.slice(2);
if (!out || inputs.length === 0) { console.error('usage: node build-names.js <out.json> <export.json>...'); process.exit(1); }
const docs = inputs.map(p => JSON.parse(fs.readFileSync(p, 'utf8')).Data.RootChunk);
const journals = docs.filter(d => d.entry), onscreens = docs.filter(d => d.root);

const text = new Map();
for (const d of onscreens)
  for (const e of d.root.Data.entries)
    text.set(e.primaryKey, { t: e.femaleVariant || e.maleVariant, s: e.secondaryKey });

const keyOf = v => (typeof v === 'string' && v.startsWith('LocKey#')) ? v.slice(7) : null;
const names = {};        // LocKey number -> text
const objectives = {};   // journal path (as in trackedQuestEntry) -> objective text
const quests = {};       // journal quest path -> quest title
const keep = k => { const e = k && text.get(k); if (e && e.t) names[k] = e.t; return e && e.t; };

// journal: collect quest titles and objective descriptions with their id paths
const walk = (node, path) => {
  if (!node || typeof node !== 'object') return;
  if (Array.isArray(node)) { node.forEach(n => walk(n, path)); return; }
  const d = node.Data && node.Data.$type ? node.Data : node;
  const here = d.id ? [...path, d.id] : path;
  if (d.$type === 'gameJournalQuest') { const t = keep(keyOf(d.title && d.title.value)); if (t) quests[here.join('/')] = t; }
  if (d.$type === 'gameJournalQuestObjective') { const t = keep(keyOf(d.description && d.description.value)); if (t) objectives[here.join('/')] = t; }
  if (Array.isArray(d.entries)) d.entries.forEach(e => walk(e, here));
};
for (const j of journals) walk(j.entry, []);

// locations: the district/sub-district block plus fast-travel, apartment and codex location names
for (const [k, e] of text) {
  const n = Number(k), s = e.s || '';
  if ((n >= 10940 && n <= 10999) || /fast[tT]ravel-districts|Gameplay-Locations|codex-locations|_apart_|localizedName/.test(s)) keep(k);
}

fs.writeFileSync(out, JSON.stringify({ names, quests, objectives }));
console.log(`names ${Object.keys(names).length}, quests ${Object.keys(quests).length}, objectives ${Object.keys(objectives).length}, ${fs.statSync(out).size} bytes`);
