// ITRM Tracker – progressive enhancement for the server-rendered pages.
// No inline scripts (Content-Security-Policy: script-src 'self'); every behaviour is wired through data-* attributes.
(function () {
    "use strict";

    const $ = (sel, root = document) => root.querySelector(sel);
    const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
    const csrfToken = () => ($('input[name="__RequestVerificationToken"]') || {}).value || "";

    // ---------------------------------------------------------------- local times
    // <time class="js-local" datetime="...Z"> → viewer's locale, medium date + short time (like the original UI).
    $$("time.js-local[datetime]").forEach((el) => {
        const d = new Date(el.getAttribute("datetime"));
        if (!Number.isNaN(d.getTime())) el.textContent = d.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
    });

    // ---------------------------------------------------------------- confirmations, auto-submit, row links
    document.addEventListener("submit", (e) => {
        const form = e.target;
        const msg = form.getAttribute("data-confirm");
        if (msg && !window.confirm(msg)) e.preventDefault();
    });
    document.addEventListener("click", (e) => {
        const btn = e.target.closest("[data-confirm-click]");
        if (btn && !window.confirm(btn.getAttribute("data-confirm-click"))) e.preventDefault();
    });
    $$("[data-autosubmit]").forEach((el) => el.addEventListener("change", () => el.form && el.form.submit()));
    document.addEventListener("click", (e) => {
        const row = e.target.closest("tr[data-href]");
        if (!row || e.target.closest("a, button, input, select, textarea, label, form")) return;
        window.location.href = row.getAttribute("data-href");
    });

    // Ctrl/Cmd+Enter submits comment and query editors.
    $$("textarea[data-ctrl-enter]").forEach((ta) => ta.addEventListener("keydown", (e) => {
        if ((e.ctrlKey || e.metaKey) && e.key === "Enter") {
            e.preventDefault();
            ta.form.requestSubmit ? ta.form.requestSubmit() : ta.form.submit();
        }
    }));

    // Copy-to-clipboard buttons (BR No / CR).
    async function copyText(text, html) {
        if (navigator.clipboard && window.ClipboardItem && html) {
            await navigator.clipboard.write([new ClipboardItem({
                "text/plain": new Blob([text], { type: "text/plain" }),
                "text/html": new Blob([html], { type: "text/html" }),
            })]);
            return;
        }
        if (navigator.clipboard) { await navigator.clipboard.writeText(text); return; }
        const ta = document.createElement("textarea");
        ta.value = text;
        document.body.appendChild(ta);
        ta.select();
        document.execCommand("copy");
        ta.remove();
    }
    function flash(btn, text) {
        const old = btn.textContent;
        btn.textContent = text;
        setTimeout(() => { btn.textContent = old; }, 1200);
    }
    $$("[data-copy-value]").forEach((btn) => btn.addEventListener("click", async () => {
        try { await copyText(btn.getAttribute("data-copy-value")); flash(btn, "\u2713"); } catch (err) { alert("Copy failed: " + err.message); }
    }));

    // ---------------------------------------------------------------- database heartbeat (every 20 s)
    const banner = $("#dbBanner");
    if (banner) {
        let lost = false;
        const beat = async () => {
            try {
                const res = await fetch("/healthz", { cache: "no-store" });
                const ok = res.ok;
                banner.classList.toggle("d-none", ok);
                if (ok && lost) window.location.reload();
                lost = !ok;
            } catch {
                banner.classList.remove("d-none");
                lost = true;
            }
        };
        setInterval(beat, 20000);
    }

    // ---------------------------------------------------------------- register: columns, density, selection
    const table = $("#registerTable");
    const chooser = $("#colChooser");
    function visibleColumns() {
        if (!chooser) return [];
        return $$('input[type="checkbox"]', chooser).filter((cb) => cb.checked).map((cb) => cb.value);
    }
    if (table && chooser) {
        const key = chooser.getAttribute("data-storage-key");
        const boxes = $$('input[type="checkbox"]', chooser);
        let saved = null;
        try { saved = JSON.parse(localStorage.getItem(key)); } catch { saved = null; }
        boxes.forEach((cb) => {
            cb.checked = cb.disabled || (Array.isArray(saved) ? saved.includes(cb.value) : cb.getAttribute("data-default") === "1");
        });
        const apply = () => {
            const visible = new Set(visibleColumns());
            $$("[data-col]", table).forEach((cell) => cell.classList.toggle("col-hidden", !visible.has(cell.getAttribute("data-col"))));
            $$("[data-visible-columns]").forEach((input) => { input.value = [...visible].join(","); });
        };
        boxes.forEach((cb) => cb.addEventListener("change", () => {
            localStorage.setItem(key, JSON.stringify(visibleColumns()));
            apply();
        }));
        const reset = $("[data-cols-reset]", chooser);
        if (reset) reset.addEventListener("click", () => {
            localStorage.removeItem(key);
            boxes.forEach((cb) => { cb.checked = cb.disabled || cb.getAttribute("data-default") === "1"; });
            apply();
        });
        apply();

        // Row density (Comfortable / Compact), persisted like the original.
        const DENSITY = "itrm_density";
        const setDensity = (mode) => {
            table.classList.toggle("density-compact", mode === "compact");
            $$("[data-density]").forEach((b) => b.classList.toggle("active", b.getAttribute("data-density") === mode));
            localStorage.setItem(DENSITY, mode);
        };
        $$("[data-density]").forEach((b) => b.addEventListener("click", () => setDensity(b.getAttribute("data-density"))));
        setDensity(localStorage.getItem(DENSITY) === "compact" ? "compact" : "comfortable");

        // Selection + bulk bar.
        const bar = $("#bulkBar");
        const selectAll = $("[data-select-all]", table);
        const rowBoxes = () => $$(".row-select", table);
        const selectedIds = () => rowBoxes().filter((cb) => cb.checked).map((cb) => cb.value);
        const refresh = () => {
            const ids = selectedIds();
            bar.classList.toggle("d-none", ids.length === 0);
            $$("[data-selected-count]").forEach((el) => { el.textContent = ids.length; });
            $$("[data-selection-form]").forEach((form) => {
                const holder = $("[data-selection-ids]", form);
                holder.innerHTML = "";
                ids.forEach((id) => {
                    const input = document.createElement("input");
                    input.type = "hidden";
                    input.name = "Ids";
                    input.value = id;
                    holder.appendChild(input);
                });
            });
            const subject = $("#emailSubject");
            if (subject && subject.dataset.defaultSubject) {
                subject.value = subject.dataset.defaultSubject.replace("{n}", ids.length).replace("{s}", ids.length === 1 ? "" : "s");
            }
            if (selectAll) selectAll.checked = ids.length > 0 && ids.length === rowBoxes().length;
        };
        if (selectAll) selectAll.addEventListener("change", () => { rowBoxes().forEach((cb) => { cb.checked = selectAll.checked; }); refresh(); });
        table.addEventListener("change", (e) => { if (e.target.classList.contains("row-select")) refresh(); });
        const clear = $("[data-clear-selection]");
        if (clear) clear.addEventListener("click", () => { rowBoxes().forEach((cb) => { cb.checked = false; }); refresh(); });
        refresh();

        // Copy table of the selected rows (e-mail dialog): HTML + plain text of the visible cells.
        const copySelected = $("[data-copy-selected]");
        if (copySelected) copySelected.addEventListener("click", async () => {
            const visible = new Set(visibleColumns());
            const headers = $$("thead th[data-col]", table).filter((th) => visible.has(th.dataset.col));
            const rows = rowBoxes().filter((cb) => cb.checked).map((cb) => cb.closest("tr"));
            const cellsOf = (tr) => $$("td[data-col]", tr).filter((td) => visible.has(td.dataset.col)).map((td) => td.textContent.replace(/\s+/g, " ").trim());
            const text = [headers.map((h) => h.textContent.trim()).join("\t"), ...rows.map((r) => cellsOf(r).join("\t"))].join("\n");
            const esc = (s) => s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
            const html = `<table border="1" style="border-collapse:collapse"><tr>${headers.map((h) => `<th>${esc(h.textContent.trim())}</th>`).join("")}</tr>${rows.map((r) => `<tr>${cellsOf(r).map((c) => `<td>${esc(c)}</td>`).join("")}</tr>`).join("")}</table>`;
            try { await copyText(text, html); flash(copySelected, "Copied"); } catch (err) { alert("Copy failed: " + err.message); }
        });
    }

    // Export / copy menus carry the current filters + visible columns.
    const withQuery = (base, extra) => {
        const params = new URLSearchParams(window.location.search);
        Object.entries(extra).forEach(([k, v]) => params.set(k, v));
        return `${base}?${params.toString()}`;
    };
    $$("[data-export]").forEach((a) => a.addEventListener("click", (e) => {
        e.preventDefault();
        window.location.href = withQuery(a.dataset.url, { mode: a.dataset.export, cols: visibleColumns().join(",") });
    }));
    $$("[data-copy]").forEach((a) => a.addEventListener("click", async (e) => {
        e.preventDefault();
        try {
            const res = await fetch(withQuery(a.dataset.url, { mode: a.dataset.copy, cols: visibleColumns().join(",") }), { credentials: "same-origin" });
            if (!res.ok) throw new Error(res.statusText);
            const text = await res.text();
            if (text.split("\n").length <= 1) { alert("No rows to copy."); return; }
            await copyText(text);
            const btn = $("#copyTableBtn");
            if (btn) flash(btn, "Copied");
        } catch (err) {
            alert("Copy failed: " + err.message);
        }
    }));

    // ---------------------------------------------------------------- multi-select filters (search / Only / Exclude)
    $$("[data-multiselect]").forEach((ms) => {
        const search = $("[data-ms-search]", ms);
        if (search) search.addEventListener("input", () => {
            const term = search.value.trim().toLowerCase();
            $$(".ms-option", ms).forEach((opt) => opt.classList.toggle("d-none", !!term && !opt.dataset.text.includes(term)));
        });
        ms.addEventListener("click", (e) => {
            const only = e.target.closest("[data-ms-only]");
            const exclude = e.target.closest("[data-ms-exclude]");
            if (!only && !exclude) return;
            const mine = $('input[type="checkbox"]', e.target.closest(".ms-option"));
            $$('input[type="checkbox"]', ms).forEach((cb) => { cb.checked = only ? cb === mine : cb !== mine; });
        });
    });

    // ---------------------------------------------------------------- saved filters (localStorage)
    const saved = $("#savedFilters");
    if (saved) {
        const KEY = "itrm_saved_filters_v2";
        const scope = saved.dataset.scope;
        const load = () => { try { const v = JSON.parse(localStorage.getItem(KEY)); return Array.isArray(v) ? v : []; } catch { return []; } };
        const list = $("[data-saved-list]", saved);
        const render = () => {
            const items = load().filter((f) => f.scope === scope);
            list.innerHTML = "";
            if (!items.length) { list.innerHTML = '<li class="text-muted small">No saved filters.</li>'; return; }
            items.forEach((f) => {
                const li = document.createElement("li");
                li.className = "d-flex justify-content-between align-items-center gap-2";
                const a = document.createElement("a");
                a.href = `${window.location.pathname}?${f.query}`;
                a.textContent = f.name;
                const del = document.createElement("button");
                del.type = "button";
                del.className = "btn btn-sm btn-link text-danger p-0";
                del.textContent = "\u00d7";
                del.setAttribute("aria-label", `Delete saved filter ${f.name}`);
                del.addEventListener("click", () => { localStorage.setItem(KEY, JSON.stringify(load().filter((x) => !(x.scope === scope && x.name === f.name)))); render(); });
                li.append(a, del);
                list.appendChild(li);
            });
        };
        const nameInput = $("[data-saved-name]", saved);
        const save = () => {
            const name = nameInput.value.trim();
            if (!name) return;
            const all = load().filter((x) => !(x.scope === scope && x.name === name));
            all.push({ scope, name, query: window.location.search.replace(/^\?/, "") });
            localStorage.setItem(KEY, JSON.stringify(all));
            nameInput.value = "";
            render();
        };
        $("[data-saved-save]", saved).addEventListener("click", save);
        nameInput.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); save(); } });
        render();
    }

    // ---------------------------------------------------------------- request form: dependencies, date order, quick add
    const form = $("[data-pipeline-form]");
    if (form) {
        // Flags enable their dependent fields (estimated → dev..deployment dates, BA required → analysis dates,
        // regulatory → regulator + deadline). Read-only rather than disabled so values are still posted.
        const sync = (clear) => {
            $$("[data-controls]", form).forEach((flag) => {
                $$(`[data-depends-on="${flag.name}"]`, form).forEach((el) => {
                    const on = flag.checked;
                    if (el.tagName === "SELECT") el.classList.toggle("pe-none", !on); else el.readOnly = !on;
                    el.setAttribute("aria-disabled", String(!on));
                    el.closest("div").classList.toggle("field-disabled", !on);
                    if (!on && clear) el.value = "";
                });
            });
        };
        $$("[data-controls]", form).forEach((flag) => flag.addEventListener("change", () => { sync(true); validateDates(); }));
        sync(false);

        // Two-sided pipeline date order (same rule as the server).
        const errorsEl = $("#dateErrors");
        function validateDates() {
            const fields = $$(".pipeline-date", form);
            const errors = [];
            fields.forEach((el, i) => {
                const v = el.value;
                if (!v) return;
                const prev = fields[i - 1];
                const next = fields[i + 1];
                if (prev && prev.value && v < prev.value) errors.push(`${el.dataset.label} must be on or after ${prev.dataset.label}.`);
                if (next && next.value && v > next.value) errors.push(`${el.dataset.label} must be on or before ${next.dataset.label}.`);
            });
            errorsEl.innerHTML = errors.map((e) => `<div>${e}</div>`).join("");
            errorsEl.classList.toggle("d-none", errors.length === 0);
            return errors;
        }
        $$(".pipeline-date", form).forEach((el) => el.addEventListener("change", validateDates));
        form.addEventListener("submit", (e) => { if (validateDates().length) { e.preventDefault(); errorsEl.scrollIntoView({ behavior: "smooth" }); } });

        // "+" next to lookup fields: add a value without leaving the form.
        $$("[data-quick-add]").forEach((btn) => btn.addEventListener("click", async () => {
            const name = (window.prompt("New value:") || "").trim();
            if (!name) return;
            try {
                const res = await fetch("/Lookups/QuickAdd", {
                    method: "POST",
                    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrfToken() },
                    body: JSON.stringify({ type: btn.dataset.quickAdd, name }),
                });
                const data = await res.json();
                if (!res.ok) throw new Error(data.error || res.statusText);
                const select = document.getElementById(btn.dataset.target);
                const opt = new Option(data.name, data.id, true, true);
                select.add(opt);
            } catch (err) {
                alert(err.message);
            }
        }));
    }

    // ---------------------------------------------------------------- stage SLA action dialog
    const stageModal = $("#stageActionModal");
    if (stageModal) {
        stageModal.addEventListener("show.bs.modal", (e) => {
            const btn = e.relatedTarget;
            const fields = (btn.dataset.fields || "").split(",");
            $("#stageActionTitle").textContent = btn.dataset.title;
            $('[data-field="stage"]', stageModal).value = btn.dataset.stage;
            $('[data-field="status"]', stageModal).value = btn.dataset.status;
            ["planned", "actual", "note"].forEach((g) => {
                const group = $(`[data-group="${g}"]`, stageModal);
                group.classList.toggle("d-none", !fields.includes(g));
                $$("input, textarea", group).forEach((i) => { i.value = ""; i.disabled = !fields.includes(g); });
            });
            const planned = $('[data-field="planned"]', stageModal);
            planned.required = fields.includes("planned");
            if (fields.includes("planned") && btn.dataset.planned) planned.value = btn.dataset.planned;
            const danger = btn.dataset.danger === "1";
            $('[data-group="danger"]', stageModal).classList.toggle("d-none", !danger);
            const submit = $('[data-field="submit"]', stageModal);
            submit.classList.toggle("btn-danger", danger);
            submit.classList.toggle("btn-primary", !danger);
        });
    }

    // ---------------------------------------------------------------- SLA settings: add/remove rows, reindex on submit
    const reindexForm = $("[data-reindex-form]");
    if (reindexForm) {
        reindexForm.addEventListener("click", (e) => {
            const add = e.target.closest("[data-add-row]");
            const remove = e.target.closest("[data-remove-row]");
            if (add) {
                const name = add.dataset.addRow;
                const tpl = $(`template[data-row-template="${name}"]`, reindexForm);
                $(`table[data-collection="${name}"] tbody`, reindexForm).appendChild(tpl.content.cloneNode(true));
            }
            if (remove) remove.closest("tr").remove();
        });
        reindexForm.addEventListener("submit", () => {
            $$("table[data-collection]", reindexForm).forEach((t) => {
                const name = t.dataset.collection;
                $$("tbody tr[data-row]", t).forEach((row, i) => {
                    $$("[name]", row).forEach((el) => { el.name = el.name.replace(new RegExp(`Form\\.${name}\\[\\d+\\]`), `Form.${name}[${i}]`); });
                });
            });
        });
    }

    // ---------------------------------------------------------------- import: dropzone and bulk decisions
    $$("[data-dropzone]").forEach((zone) => {
        const input = $('input[type="file"]', zone);
        ["dragenter", "dragover"].forEach((ev) => zone.addEventListener(ev, (e) => { e.preventDefault(); zone.classList.add("dragover"); }));
        ["dragleave", "drop"].forEach((ev) => zone.addEventListener(ev, () => zone.classList.remove("dragover")));
        zone.addEventListener("drop", (e) => { e.preventDefault(); if (e.dataTransfer.files.length) input.files = e.dataTransfer.files; });
    });
    $$("[data-decide-all]").forEach((btn) => btn.addEventListener("click", () => {
        $$("select[data-suggested]").forEach((sel) => { sel.value = btn.dataset.decideAll === "skip" ? "skip" : sel.dataset.suggested; });
    }));
})();
