async dbs => {
  const bytes = s => {
    const b = atob(s);
    const u = new Uint8Array(b.length);
    for (let i = 0; i < b.length; i++) u[i] = b.charCodeAt(i);
    return u;
  };
  const dec = v => {
    if (Array.isArray(v)) return v.map(dec);
    if (v === null || typeof v !== "object") return v;
    if ("$bigint" in v) return BigInt(v.$bigint);
    if ("$number" in v) return Number(v.$number);
    if ("$undefined" in v) return undefined;
    if ("$date" in v) return new Date(v.$date);
    if ("$blob" in v) {
      const u = bytes(v.data);
      return v.name !== undefined ? new File([u], v.name, { type: v.$blob }) : new Blob([u], { type: v.$blob });
    }
    if ("$bytes" in v) {
      const u = bytes(v.data);
      if (v.$bytes === "ArrayBuffer") return u.buffer;
      const T = globalThis[v.$bytes];
      return typeof T === "function" ? new T(u.buffer) : u;
    }
    if ("$handle" in v) return null;
    if ("$map" in v) return new Map(v.$map.map(([k, x]) => [dec(k), dec(x)]));
    if ("$set" in v) return new Set(v.$set.map(dec));
    const o = {};
    for (const k of Object.keys(v)) o[k] = dec(v[k]);
    return o;
  };
  for (const d of dbs) {
    await new Promise((res, rej) => {
      const r = indexedDB.deleteDatabase(d.name);
      r.onsuccess = res;
      r.onerror = () => rej(r.error);
    });
    const db = await new Promise((res, rej) => {
      const r = indexedDB.open(d.name, d.version);
      r.onupgradeneeded = () => {
        for (const s of d.stores) {
          const store = r.result.createObjectStore(s.name, { keyPath: s.keyPath === null ? undefined : s.keyPath, autoIncrement: s.autoIncrement });
          for (const i of s.indexes) store.createIndex(i.name, i.keyPath, { unique: i.unique, multiEntry: i.multiEntry });
        }
      };
      r.onsuccess = () => res(r.result);
      r.onerror = () => rej(r.error);
    });
    for (const s of d.stores) {
      await new Promise((res, rej) => {
        const t = db.transaction(s.name, "readwrite");
        const store = t.objectStore(s.name);
        for (const rec of s.records) {
          if (s.keyPath === null) store.put(dec(rec.value), dec(rec.key));
          else store.put(dec(rec.value));
        }
        t.oncomplete = res;
        t.onerror = () => rej(t.error);
        t.onabort = () => rej(t.error);
      });
    }
    db.close();
  }
  return true;
}
