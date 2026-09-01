const state = { rows: [], selected: null };

const $ = (id) => document.getElementById(id);

function todayISO() {
  return new Date().toISOString().slice(0, 10);
}

function daysAgoISO(days) {
  const d = new Date();
  d.setDate(d.getDate() - days);
  return d.toISOString().slice(0, 10);
}

function toast(msg) {
  const el = $("toast");
  el.textContent = msg;
  el.hidden = false;
  clearTimeout(toast._t);
  toast._t = setTimeout(() => { el.hidden = true; }, 4200);
}

function formPayload() {
  const f = $("filterForm");
  return {
    from_date: f.from_date.value || null,
    to_date: f.to_date.value || null,
    order_num: f.order_num.value.trim(),
    order_index: f.order_index.value,
    limit: f.limit.value || 100,
    only_flipped: f.only_flipped.checked,
    only_custom: f.only_custom.checked,
  };
}

function statusLabel(row) {
  if (row.status === "ok") return row.match ? "Compared" : "No match";
  if (row.status === "missing_actual_dxf") return "No original";
  if (row.status === "missing_generated_dxf") return "No export";
  if (row.status === "compare_error") return "Error";
  return row.status;
}

function badge(kind, text) {
  return `<span class="badge badge-${kind}">${text || "—"}</span>`;
}

function fmtRot(row) {
  if (row.status !== "ok" || row.rotation_ccw == null) return "—";
  const n = Number(row.rotation_ccw);
  return `${n.toFixed(n % 1 === 0 ? 0 : 1)}°`;
}

function drawShapes(canvas, actual, generated) {
  const ctx = canvas.getContext("2d");
  const w = canvas.width;
  const h = canvas.height;
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = "#071428";
  ctx.fillRect(0, 0, w, h);

  const groups = [actual || [], generated || []].filter((g) => g.length);
  if (!groups.length) {
    ctx.fillStyle = "#8b9bb8";
    ctx.font = "14px Manrope, sans-serif";
    ctx.fillText("No outline to draw", 24, h / 2);
    return;
  }

  const all = groups.flat();
  const xs = all.map((p) => p.x);
  const ys = all.map((p) => p.y);
  const minX = Math.min(...xs);
  const maxX = Math.max(...xs);
  const minY = Math.min(...ys);
  const maxY = Math.max(...ys);
  const spanX = Math.max(maxX - minX, 1e-6);
  const spanY = Math.max(maxY - minY, 1e-6);
  const pad = 28;
  const scale = Math.min((w - pad * 2) / spanX, (h - pad * 2) / spanY);

  const map = (p) => ({
    x: pad + (p.x - minX) * scale,
    y: h - pad - (p.y - minY) * scale,
  });

  const stroke = (pts, color, dash) => {
    if (!pts.length) return;
    ctx.beginPath();
    const first = map(pts[0]);
    ctx.moveTo(first.x, first.y);
    for (let i = 1; i < pts.length; i++) {
      const q = map(pts[i]);
      ctx.lineTo(q.x, q.y);
    }
    ctx.closePath();
    ctx.strokeStyle = color;
    ctx.lineWidth = 2.2;
    ctx.setLineDash(dash || []);
    ctx.stroke();
  };

  stroke(actual || [], "#5ec8ff", []);
  stroke(generated || [], "#4d8dff", [7, 5]);

  ctx.setLineDash([]);
  ctx.font = "12px IBM Plex Mono, monospace";
  ctx.fillStyle = "#5ec8ff";
  ctx.fillText("original", 16, 22);
  ctx.fillStyle = "#4d8dff";
  ctx.fillText("generated", 100, 22);
}

function renderStats(summary) {
  $("statTotal").textContent = summary?.total ?? 0;
  $("statCompared").textContent = summary?.compared ?? 0;
  $("statMatched").textContent = summary?.matched ?? 0;
  $("statFlipped").textContent = summary?.flipped ?? 0;
  $("statMissing").textContent = summary?.missing_actual ?? 0;
  $("statMissingGen").textContent = summary?.missing_generated ?? 0;
}

function visibleRows() {
  const q = $("searchBox").value.trim().toLowerCase();
  if (!q) return state.rows;
  return state.rows.filter((r) =>
    `${r.order_num} ${r.shape_id} ${r.shape_name} ${r.unique_id} ${r.db_flipping_side} ${r.flip_side} ${r.status}`
      .toLowerCase()
      .includes(q)
  );
}

function renderTable() {
  const body = $("rows");
  const rows = visibleRows();
  if (!rows.length) {
    body.innerHTML = `<tr><td colspan="10" class="empty">No shapes to show.</td></tr>`;
    return;
  }
  body.innerHTML = rows.map((r, i) => {
    const detected = r.status === "ok" ? (r.flipped ? r.flip_side : "None") : "—";
    const matchKind = r.status !== "ok" ? "miss" : r.match ? "ok" : "no";
    const flipKind = (r.db_flipping_side || r.flipped) ? "flip" : "miss";
    return `<tr data-i="${state.rows.indexOf(r)}" class="${r.flipped || r.db_flipping_side ? "is-flip" : ""}">
      <td>${(r.order_date || "").slice(0, 16)}</td>
      <td>${r.order_num}</td>
      <td>${r.glass_line_no}</td>
      <td>${r.shape_name || "—"}</td>
      <td>${r.width && r.height ? `${r.width} × ${r.height}` : "—"}</td>
      <td>${r.db_flipping_side ? badge("flip", r.db_flipping_side) : "—"}</td>
      <td>${r.status === "ok" ? badge(r.flipped ? "flip" : "miss", detected) : "—"}</td>
      <td>${fmtRot(r)}</td>
      <td>${badge(matchKind, r.status === "ok" ? (r.match ? "Yes" : "No") : "—")}</td>
      <td>${badge(r.status === "missing_actual_dxf" ? "miss" : matchKind, statusLabel(r))}</td>
    </tr>`;
  }).join("");
}

function facts(list) {
  return list
    .filter(([, v]) => v !== "" && v != null)
    .map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`)
    .join("");
}

function openDrawer(row) {
  state.selected = row;
  $("drawer").hidden = false;
  $("scrim").hidden = false;
  $("drawerKicker").textContent = `Order ${row.order_num} · line ${row.glass_line_no}`;
  $("drawerTitle").textContent = row.transform || statusLabel(row);
  const img = $("drawerImage");
  img.hidden = true;
  img.onload = () => { img.hidden = false; };
  img.onerror = () => { img.hidden = true; };
  img.src = `/api/shape/${row.shape_id}/image`;
  drawShapes($("drawerCanvas"), row.actual_points, row.generated_points);
  $("drawerFacts").innerHTML = facts([
    ["Unique ID", row.unique_id || "—"],
    ["Shape id", row.shape_id],
    ["Order index", row.order_index],
    ["Entered", (row.entered_datetime || "").slice(0, 16)],
    ["DB flip", row.db_flipping_side || "None"],
    ["XML flip", row.xml_flipping_side || "—"],
    ["Pattern", [row.xml_pattern_flip, row.xml_pattern_rotate, row.xml_pattern_side].filter(Boolean).join(" / ") || "—"],
    ["Detected", row.flip_description || "—"],
    ["Rotation CCW", row.rotation_ccw == null ? "—" : `${row.rotation_ccw}°`],
    ["Fit error", row.fit_error == null ? "—" : Number(row.fit_error).toExponential(3)],
    ["Vertices", row.vertex_count || "—"],
    ["Message", row.message || ""],
  ]);
  const uid = row.unique_id || "UniqueID";
  $("drawerPaths").textContent = [
    row.actual_dxf
      ? `Original (layer 3): ${row.actual_dxf}`
      : (row.message || `Original DXF not found (${uid}.dxf layer 3)`),
    row.generated_dxf
      ? `Exported (${row.generated_source || "dxf"}): ${row.generated_dxf}`
      : `Exported DXF not found (${uid}optiDxf.dxf)`,
  ].join("\n");
}

function closeDrawer() {
  $("drawer").hidden = true;
  $("scrim").hidden = true;
}

async function loadStatus() {
  try {
    const res = await fetch("/api/status");
    const data = await res.json();
    const pill = $("dbStatus");
    if (data.ok) {
      pill.textContent = `${data.server} · ${data.database}`;
      pill.className = "pill pill-ok";
      $("dbRange").textContent = `${data.shapes.toLocaleString()} shapes · ${data.first_date} → ${data.last_date} · ${data.uploads_files ?? 0} DXF in uploads`;
    } else {
      pill.textContent = "Database offline";
      pill.className = "pill pill-bad";
      $("dbRange").textContent = data.error || "";
    }
  } catch (err) {
    $("dbStatus").textContent = "Database offline";
    $("dbStatus").className = "pill pill-bad";
  }
}

async function analyse(ev) {
  ev?.preventDefault();
  const btn = $("filterForm").querySelector("button[type=submit]");
  btn.disabled = true;
  $("tableHint").textContent = "Reading KRISTAL and comparing DXF files…";
  try {
    const res = await fetch("/api/analyse", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(formPayload()),
    });
    const data = await res.json();
    if (!res.ok || !data.ok) throw new Error(data.error || "Analyse failed");
    state.rows = data.rows || [];
    renderStats(data.summary);
    renderTable();
    $("tableHint").textContent = `${state.rows.length} row(s), sorted by date and order.`;
  } catch (err) {
    toast(err.message);
    $("tableHint").textContent = err.message;
  } finally {
    btn.disabled = false;
  }
}

async function exportCsv() {
  try {
    const res = await fetch("/api/export.csv", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(formPayload()),
    });
    if (!res.ok) throw new Error("Export failed");
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = "spil-dxf-analysis.csv";
    a.click();
    URL.revokeObjectURL(url);
  } catch (err) {
    toast(err.message);
  }
}

async function compareFiles(ev) {
  ev.preventDefault();
  const form = $("compareForm");
  const actual = form.actual.files[0];
  const generated = form.generated.files[0];
  if (!actual || !generated) {
    toast("Choose both DXF files.");
    return;
  }
  const body = new FormData();
  body.append("actual", actual);
  body.append("generated", generated);
  try {
    const res = await fetch("/api/compare", { method: "POST", body });
    const data = await res.json();
    if (!res.ok || data.ok === false) throw new Error(data.error || "Compare failed");
    $("compareResult").hidden = false;
    $("compareLabel").textContent = data.match ? "Match" : "No match";
    $("compareTransform").textContent = data.transform || data.message;
    $("compareFacts").innerHTML = facts([
      ["Original", data.actual_name],
      ["Generated", data.generated_name],
      ["Flipped", data.flipped ? data.flip_description : "No"],
      ["Rotation CCW", `${data.rotation_ccw}°`],
      ["Rotation CW", `${data.rotation_cw}°`],
      ["Vertices", data.vertex_count],
      ["Fit error", data.fit_error == null ? "—" : Number(data.fit_error).toExponential(3)],
    ]);
    drawShapes($("compareCanvas"), data.actual_points, data.generated_points);
  } catch (err) {
    toast(err.message);
  }
}

function bindDrops() {
  [["dropActual", "nameActual", "actual"], ["dropGenerated", "nameGenerated", "generated"]].forEach(
    ([id, nameId, inputName]) => {
      const box = $(id);
      const input = $("compareForm")[inputName];
      const label = $(nameId);
      const setName = () => {
        label.textContent = input.files[0]?.name || "Drop or browse";
        box.classList.toggle("is-on", Boolean(input.files[0]));
      };
      input.addEventListener("change", setName);
      box.addEventListener("dragover", (e) => { e.preventDefault(); box.classList.add("is-on"); });
      box.addEventListener("dragleave", () => { if (!input.files[0]) box.classList.remove("is-on"); });
      box.addEventListener("drop", (e) => {
        e.preventDefault();
        if (e.dataTransfer.files[0]) {
          const dt = new DataTransfer();
          dt.items.add(e.dataTransfer.files[0]);
          input.files = dt.files;
          setName();
        }
      });
    }
  );
}

function pad2(n) {
  return String(n).padStart(2, "0");
}

function parseISODate(value) {
  if (!value) return null;
  const [y, m, d] = value.split("-").map(Number);
  if (!y || !m || !d) return null;
  return new Date(y, m - 1, d);
}

function bindDatePickers() {
  const pop = $("datePop");
  if (!pop) return;
  let activeInput = null;
  let view = new Date();

  function closePop() {
    pop.hidden = true;
    activeInput = null;
  }

  function render() {
    const year = view.getFullYear();
    const month = view.getMonth();
    const startDow = new Date(year, month, 1).getDay();
    const daysInMonth = new Date(year, month + 1, 0).getDate();
    const selected = parseISODate(activeInput?.value);
    const today = new Date();
    const title = view.toLocaleString("en-GB", { month: "long", year: "numeric" });
    const cells = [];
    for (let i = 0; i < startDow; i += 1) cells.push('<span class="date-cell is-pad"></span>');
    for (let day = 1; day <= daysInMonth; day += 1) {
      const iso = `${year}-${pad2(month + 1)}-${pad2(day)}`;
      const sel = selected && selected.getFullYear() === year && selected.getMonth() === month && selected.getDate() === day;
      const isToday = today.getFullYear() === year && today.getMonth() === month && today.getDate() === day;
      cells.push(
        `<button type="button" class="date-cell${sel ? " is-sel" : ""}${isToday ? " is-today" : ""}" data-iso="${iso}">${day}</button>`
      );
    }
    pop.innerHTML = `
      <div class="date-pop-head">
        <button type="button" data-nav="-1" aria-label="Previous month">‹</button>
        <strong>${title}</strong>
        <button type="button" data-nav="1" aria-label="Next month">›</button>
      </div>
      <div class="date-dow">${["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"].map((d) => `<span>${d}</span>`).join("")}</div>
      <div class="date-grid">${cells.join("")}</div>
    `;
  }

  function openPop(input) {
    activeInput = input;
    view = parseISODate(input.value) || new Date();
    render();
    pop.hidden = false;
    const wrap = input.closest(".date-wrap") || input;
    const rect = wrap.getBoundingClientRect();
    const left = Math.max(8, Math.min(rect.left, window.innerWidth - 288));
    pop.style.left = `${left}px`;
    pop.style.top = `${rect.bottom + 6}px`;
  }

  pop.addEventListener("click", (e) => {
    const nav = e.target.closest("[data-nav]");
    if (nav) {
      view = new Date(view.getFullYear(), view.getMonth() + Number(nav.dataset.nav), 1);
      render();
      return;
    }
    const cell = e.target.closest(".date-cell[data-iso]");
    if (cell && activeInput) {
      activeInput.value = cell.dataset.iso;
      closePop();
    }
  });

  document.addEventListener("pointerdown", (e) => {
    if (pop.hidden) return;
    if (pop.contains(e.target) || e.target.closest(".date-wrap")) return;
    closePop();
  });

  document.querySelectorAll(".date-wrap").forEach((wrap) => {
    const input = wrap.querySelector('input[type="date"]');
    const btn = wrap.querySelector(".date-btn");
    const toggle = (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (activeInput === input && !pop.hidden) closePop();
      else openPop(input);
    };
    input.addEventListener("mousedown", toggle);
    btn.addEventListener("click", toggle);
    input.addEventListener("keydown", (e) => {
      if (e.key === "ArrowDown" || e.key === " ") {
        e.preventDefault();
        openPop(input);
      }
    });
  });

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && !pop.hidden) closePop();
  });
}

function initDates() {
  const f = $("filterForm");
  f.from_date.value = daysAgoISO(14);
  f.to_date.value = todayISO();
}

document.querySelectorAll(".tab").forEach((btn) => {
  btn.addEventListener("click", () => {
    document.querySelectorAll(".tab").forEach((b) => b.classList.toggle("is-on", b === btn));
    $("panel-orders").classList.toggle("is-on", btn.dataset.tab === "orders");
    $("panel-files").classList.toggle("is-on", btn.dataset.tab === "files");
  });
});

$("filterForm").addEventListener("submit", analyse);
$("exportBtn").addEventListener("click", exportCsv);
$("searchBox").addEventListener("input", renderTable);
$("compareForm").addEventListener("submit", compareFiles);
$("drawerClose").addEventListener("click", closeDrawer);
$("scrim").addEventListener("click", closeDrawer);
document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeDrawer(); });
$("rows").addEventListener("click", (e) => {
  const tr = e.target.closest("tr[data-i]");
  if (!tr) return;
  openDrawer(state.rows[Number(tr.dataset.i)]);
});

bindDrops();
initDates();
bindDatePickers();
loadStatus();
