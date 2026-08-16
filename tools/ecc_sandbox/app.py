"""Sandbox UI: chon cap -> tien xu ly -> PyramidECC -> xem ma tran + ket qua match."""
import os
import sys
import traceback
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

import cv2
import numpy as np
from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg
from matplotlib.figure import Figure

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import config as cfg_mod
import pairs as pairs_mod
import preprocess as pre
import pyramid_ecc as ecc

PREVIEW = 512


def thumb(img):
    if img is None:
        return np.zeros((8, 8), dtype=np.uint8)
    scale = PREVIEW / float(max(img.shape[0], img.shape[1]))
    if scale >= 1.0:
        return img
    return cv2.resize(img, (int(img.shape[1] * scale), int(img.shape[0] * scale)),
                      interpolation=cv2.INTER_AREA)


class App(object):
    def __init__(self, root):
        self.root = root
        root.title("RDL.GerberStitch — ECC / Preprocessing Sandbox")
        self.cfg = cfg_mod.load(self._guess_ini())
        self.payload = None
        self.raster = None
        self.vars = {}

        outer = ttk.Frame(root, padding=6)
        outer.pack(fill="both", expand=True)

        # Panel trai: khung cuon doc, be rong co dinh (~cua so tham so). Khong ep chieu rong noi
        # dung theo canvas -- cac hang Entry/Label da tu co chieu rong tu nhien.
        left_container, left = self._make_scrollable(outer, stretch_width=False)
        left_container.pack(side="left", fill="y", padx=(0, 8))
        left_container.configure(width=300)
        left_container.pack_propagate(False)

        # Panel phai: nut CHAY dong o TREN CUNG (luon thay, khong can cuon), phia duoi la khung
        # cuon doc chua Notebook 3 tab -- de bieu do/anh khong bao gio bi cat neu man hinh thap.
        right = ttk.Frame(outer)
        right.pack(side="left", fill="both", expand=True)
        self._build_run_bar(right)
        right_container, right_inner = self._make_scrollable(right, stretch_width=True)
        right_container.pack(side="top", fill="both", expand=True)

        self._build_inputs(left)
        self._build_params(left)
        self._build_tabs(right_inner)

    # ---------- helpers ----------
    def _guess_ini(self):
        here = os.path.dirname(os.path.abspath(__file__))
        candidate = os.path.abspath(os.path.join(
            here, "..", "..", "RDL.GerberStitch.Harness", "align_stitch.ini"))
        return candidate if os.path.exists(candidate) else None

    def _make_scrollable(self, parent, stretch_width):
        """Canvas + Scrollbar doc chuan cua Tkinter (ttk.Frame khong tu ho tro cuon).

        stretch_width=True: noi dung ben trong gian ra dung bang be rong canvas (dung cho panel
        phai, noi Notebook/bieu do can chiem het chieu rong con lai). stretch_width=False: giu
        chieu rong tu nhien cua noi dung (dung cho panel trai, cac hang Entry/Label co do rong co
        dinh, khong can gian).
        """
        container = ttk.Frame(parent)
        canvas = tk.Canvas(container, highlightthickness=0)
        vsb = ttk.Scrollbar(container, orient="vertical", command=canvas.yview)
        canvas.configure(yscrollcommand=vsb.set)
        canvas.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")

        inner = ttk.Frame(canvas)
        inner_id = canvas.create_window((0, 0), window=inner, anchor="nw")

        def on_inner_configure(_event):
            canvas.configure(scrollregion=canvas.bbox("all"))

        inner.bind("<Configure>", on_inner_configure)

        if stretch_width:
            def on_canvas_configure(event):
                canvas.itemconfig(inner_id, width=event.width)

            canvas.bind("<Configure>", on_canvas_configure)

        def on_mousewheel(event):
            canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")

        def bind_wheel(_event):
            canvas.bind_all("<MouseWheel>", on_mousewheel)

        def unbind_wheel(_event):
            canvas.unbind_all("<MouseWheel>")

        canvas.bind("<Enter>", bind_wheel)
        canvas.bind("<Leave>", unbind_wheel)

        return container, inner

    def _row(self, parent, label, key, default, width=10):
        frame = ttk.Frame(parent)
        frame.pack(fill="x", pady=1)
        ttk.Label(frame, text=label, width=22).pack(side="left")
        var = tk.StringVar(value=str(default))
        ttk.Entry(frame, textvariable=var, width=width).pack(side="left", fill="x", expand=True)
        self.vars[key] = var
        return var

    def _num(self, key, caster=float):
        return caster(self.vars[key].get())

    # ---------- panels ----------
    def _build_inputs(self, parent):
        box = ttk.LabelFrame(parent, text="Dữ liệu", padding=6)
        box.pack(fill="x")
        self._row(box, "Payload JSON", "payload", "", 32)
        ttk.Button(box, text="Chọn payload…", command=self.pick_payload).pack(fill="x", pady=2)
        self._row(box, "Thư mục ảnh chụp", "images", "", 32)
        ttk.Button(box, text="Chọn thư mục…", command=self.pick_images).pack(fill="x", pady=2)
        self._row(box, "Raster Gerber", "raster", "", 32)
        ttk.Button(box, text="Chọn raster…", command=self.pick_raster).pack(fill="x", pady=2)
        self._row(box, "Đuôi file ảnh", "ext", ".bmp")

        sel = ttk.LabelFrame(parent, text="Cặp kiểm tra", padding=6)
        sel.pack(fill="x", pady=(8, 0))
        self.mode = tk.StringVar(value="direct")
        ttk.Radiobutton(sel, text="Direct  (tile Gerber ↔ ảnh chụp)",
                        variable=self.mode, value="direct").pack(anchor="w")
        ttk.Radiobutton(sel, text="Neighbor (ảnh chụp ↔ ảnh chụp)",
                        variable=self.mode, value="neighbor").pack(anchor="w")
        self._row(sel, "Row", "row", 1, 6)
        self._row(sel, "Column", "col", 1, 6)
        self._row(sel, "Hướng neighbor", "dir", "right", 8)

        # "Step ghi de" la tham so DUY NHAT khong tuong ung setting nao trong C# --
        # phai NOI BAT khac cac o con lai (mau nen/vien rieng) de nguoi dung khong
        # nham no voi mot tham so pipeline that. Dung tk.Entry (khong phai ttk) de
        # co the chinh background mau truc tiep.
        step_frame = tk.Frame(sel, bg="#fff3cd", highlightbackground="#e0a800",
                              highlightthickness=1, bd=0)
        step_frame.pack(fill="x", pady=(6, 1))
        tk.Label(step_frame, text="Step ghi đè (0=payload)", width=22, bg="#fff3cd",
                fg="#7a5b00").pack(side="left")
        self.step_var = tk.StringVar(value="0")
        tk.Entry(step_frame, textvariable=self.step_var, width=10, bg="#fffdf5",
                highlightbackground="#e0a800", highlightthickness=1).pack(
            side="left", fill="x", expand=True)
        self.vars["step"] = self.step_var

    def _build_params(self, parent):
        pp = ttk.LabelFrame(parent, text="Tiền xử lý (§8.3)", padding=6)
        pp.pack(fill="x", pady=(8, 0))
        frame = ttk.Frame(pp)
        frame.pack(fill="x", pady=1)
        ttk.Label(frame, text="Preprocess mode", width=22).pack(side="left")
        self.preprocess_mode = tk.StringVar(value="FlattenAndEnhance")
        ttk.Combobox(frame, textvariable=self.preprocess_mode, width=16, state="readonly",
                     values=["FlattenAndEnhance", "ToBinaryTraces"]).pack(side="left")
        self._row(pp, "Contrast (%)", "contrast", self.cfg["Contrast"])
        self._row(pp, "Background sigma", "bgsigma", self.cfg["BackgroundSigma"])
        self._row(pp, "CLAHE clip limit", "clip", self.cfg["ClaheClipLimit"])
        self._row(pp, "CLAHE tile", "clahetile", self.cfg["ClaheTile"])
        self._row(pp, "Adaptive blockSize", "block", self.cfg["AdaptiveBlockSize"])
        self._row(pp, "Adaptive C", "cval", self.cfg["AdaptiveC"])
        self._row(pp, "Close kernel", "ck", self.cfg["CloseKernel"])

        ep = ttk.LabelFrame(parent, text="PyramidECC (từ align_stitch.ini)", padding=6)
        ep.pack(fill="x", pady=(8, 0))
        frame = ttk.Frame(ep)
        frame.pack(fill="x", pady=1)
        ttk.Label(frame, text="MotionModel", width=22).pack(side="left")
        self.motion = tk.StringVar(value=self.cfg["EccMotionModel"])
        ttk.Combobox(frame, textvariable=self.motion, width=12, state="readonly",
                     values=["Translation", "Euclidean", "Affine"]).pack(side="left")
        frame = ttk.Frame(ep)
        frame.pack(fill="x", pady=1)
        ttk.Label(frame, text="Affine scale", width=22).pack(side="left")
        self.affine_normalize = tk.StringVar(value=self.cfg["AffineNormalize"])
        ttk.Combobox(frame, textvariable=self.affine_normalize, width=12, state="readonly",
                     values=["median", "min"]).pack(side="left")
        self._row(ep, "PyramidLevels", "levels", self.cfg["EccPyramidLevels"])
        self._row(ep, "MaxIterations", "iters", self.cfg["EccMaxIterations"])
        self._row(ep, "Epsilon", "eps", self.cfg["EccEpsilon"])
        self._row(ep, "MinCorrelation", "mincorr", self.cfg["EccMinCorrelation"])
        self._row(ep, "MaxAbsRotationDeg", "maxrot", self.cfg["MaxAbsRotationDeg"])
        self._row(ep, "MaxTranslationPixels", "maxtrans", self.cfg["MaxTranslationPixels"])
        self._row(ep, "Pitch corr. X (px/step)", "pitchx", self.cfg["PitchCorrectionPxPerStepX"])
        self._row(ep, "Pitch corr. Y (px/step)", "pitchy", self.cfg["PitchCorrectionPxPerStepY"])

    def _build_run_bar(self, parent):
        # Dat rieng o dau panel phai (NGOAI khung cuon) de luon nhin thay va bam duoc ngay,
        # khong phai cuon qua toan bo panel tham so ben trai moi toi.
        bar = ttk.Frame(parent, padding=(0, 0, 0, 6))
        bar.pack(side="top", fill="x")
        ttk.Button(bar, text="CHẠY", command=self.run).pack(fill="x", ipady=4)

    def _build_tabs(self, parent):
        self.tabs = ttk.Notebook(parent)
        self.tabs.pack(fill="both", expand=True)

        self.fig_pre = Figure(figsize=(8, 4.6), dpi=90)
        self.canvas_pre = self._add_tab(self.fig_pre, "Tiền xử lý")

        self.fig_match = Figure(figsize=(8, 4.6), dpi=90)
        self.canvas_match = self._add_tab(self.fig_match, "Kết quả match")

        text_tab = ttk.Frame(self.tabs)
        self.tabs.add(text_tab, text="Ma trận & log")
        self.log = tk.Text(text_tab, wrap="none", font=("Consolas", 9))
        self.log.pack(fill="both", expand=True)

    def _add_tab(self, figure, title):
        tab = ttk.Frame(self.tabs)
        self.tabs.add(tab, text=title)
        canvas = FigureCanvasTkAgg(figure, master=tab)
        canvas.get_tk_widget().pack(fill="both", expand=True)
        return canvas

    # ---------- file pickers ----------
    def pick_payload(self):
        path = filedialog.askopenfilename(filetypes=[("JSON", "*.json")])
        if path:
            self.vars["payload"].set(path)

    def pick_images(self):
        path = filedialog.askdirectory()
        if path:
            self.vars["images"].set(path)

    def pick_raster(self):
        path = filedialog.askopenfilename(
            filetypes=[("Ảnh", "*.tif *.tiff *.png *.bmp *.jpg")])
        if path:
            self.vars["raster"].set(path)

    def say(self, text):
        self.log.insert("end", text + "\n")
        self.log.see("end")

    def _on_stage(self, stage, detail):
        self.say("  [stage] %s %s" % (stage, detail))
        self.root.update_idletasks()

    # ---------- run ----------
    def current_cfg(self):
        c = dict(self.cfg)
        c.update({
            "Contrast": self._num("contrast"),
            "BackgroundSigma": self._num("bgsigma"),
            "ClaheClipLimit": self._num("clip"),
            "ClaheTile": self._num("clahetile", int),
            "AdaptiveBlockSize": self._num("block", int),
            "AdaptiveC": self._num("cval"),
            "CloseKernel": self._num("ck", int),
            "EccMotionModel": self.motion.get(),
            "AffineNormalize": self.affine_normalize.get(),
            "EccPyramidLevels": self._num("levels", int),
            "EccMaxIterations": self._num("iters", int),
            "EccEpsilon": self._num("eps"),
            "EccMinCorrelation": self._num("mincorr"),
            "MaxAbsRotationDeg": self._num("maxrot"),
            "MaxTranslationPixels": self._num("maxtrans"),
            "PitchCorrectionPxPerStepX": self._num("pitchx"),
            "PitchCorrectionPxPerStepY": self._num("pitchy"),
        })
        return c

    def run(self):
        try:
            self._run()
        except Exception as ex:
            self.say("LỖI: " + str(ex))
            self.say(traceback.format_exc())
            messagebox.showerror("Lỗi", str(ex))

    def _run(self):
        self.log.delete("1.0", "end")
        c = self.current_cfg()
        payload_path = self.vars["payload"].get()
        images = self.vars["images"].get()
        ext = self.vars["ext"].get() or ".bmp"
        if not payload_path or not images:
            raise ValueError("Cần chọn payload JSON và thư mục ảnh chụp.")

        payload = pairs_mod.load_payload(payload_path)
        row, col = self._num("row", int), self._num("col", int)
        order = pairs_mod.index_of(payload, row, col)
        if order is None:
            raise ValueError("Không có tile ở (row=%d, col=%d)." % (row, col))

        step = self._num("step")
        step = None if step <= 0 else step

        if self.mode.get() == "direct":
            raster_path = self.vars["raster"].get()
            if not raster_path:
                raise ValueError("Mode Direct cần raster Gerber.")
            raster = pairs_mod.RasterSource(raster_path)
            reference, moving, meta = pairs_mod.direct_pair(
                payload, raster, images, order, step, ext,
                pitch_correction_px_per_step_x=c["PitchCorrectionPxPerStepX"],
                pitch_correction_px_per_step_y=c["PitchCorrectionPxPerStepY"])
            self.say("Direct: tile order=%d (row=%d, col=%d), reference origin=%s"
                     % (order, row, col, meta["reference_origin"]))
        else:
            direction = self.vars["dir"].get().strip().lower()
            delta = {"right": (0, 1), "left": (0, -1),
                     "bottom": (1, 0), "top": (-1, 0)}[direction]
            target = pairs_mod.index_of(payload, row + delta[0], col + delta[1])
            if target is None:
                raise ValueError("Không có neighbor hướng " + direction)
            reference, moving, meta = pairs_mod.neighbor_pair(payload, images, order, target, ext)
            self.say("Neighbor: anchor=%d target=%d (%s)" % (order, target, direction))

        mode = self.preprocess_mode.get()
        ref_v = pre.build_variants(reference, c, mode)
        mov_v = pre.build_variants(moving, c, mode)
        self._draw_preprocess(ref_v, mov_v)

        # Xac minh doc lap dung anh contrast, KHONG dung lai anh flattened/binary ma ECC
        # da toi uu hoa tren do (docs/superpowers spec "Independent Alignment Verification").
        result = ecc.match(ref_v["final"], mov_v["final"], c,
                           verification_reference=ref_v["contrast"],
                           verification_moving=mov_v["contrast"],
                           on_stage=self._on_stage)
        self._report(result)
        self._draw_match(ref_v["final"], mov_v["final"], result)

    def _draw_preprocess(self, ref_v, mov_v):
        keys = [k for k in ("raw", "contrast", "flattened", "binary") if k in ref_v]
        self.fig_pre.clear()
        for i, key in enumerate(keys):
            for j, (name, variants) in enumerate((("reference", ref_v), ("moving", mov_v))):
                ax = self.fig_pre.add_subplot(2, len(keys), j * len(keys) + i + 1)
                ax.imshow(thumb(variants[key]), cmap="gray", vmin=0, vmax=255)
                ax.set_title("%s · %s" % (name, key), fontsize=8)
                ax.axis("off")
        self.fig_pre.tight_layout()
        # cot "final" la dau ra CUA CHINH MOT che do da chon (FlattenAndEnhance hoac
        # ToBinaryTraces) -- day la anh duy nhat duoc dua vao ECC. Xac minh doc lap
        # (structural verification) lai dung cot "contrast", KHONG dung "final".
        self.fig_pre.text(
            0.5, 0.01,
            "Lưu ý: ECC chạy trên cột 'final' (đầu ra của chế độ tiền xử lý đã chọn) — "
            "xác minh cấu trúc độc lập lại dùng cột 'contrast', không dùng lại 'final'.",
            ha="center", va="bottom", fontsize=7, color="#b00000")
        self.canvas_pre.draw()

    def _draw_match(self, reference, moving, result):
        self.fig_match.clear()
        h, w = reference.shape
        before = cv2.merge([thumb(reference), thumb(moving), thumb(reference)])
        ax1 = self.fig_match.add_subplot(1, 2, 1)
        ax1.imshow(before)
        ax1.set_title("TRƯỚC — magenta=reference, xanh=moving", fontsize=8)
        ax1.axis("off")

        ax2 = self.fig_match.add_subplot(1, 2, 2)
        if result.get("matrix") is not None:
            warped = ecc.warp_moving_to_reference(moving, result["matrix"], (w, h))
            after = cv2.merge([thumb(reference), thumb(warped), thumb(reference)])
            ax2.imshow(after)
            ax2.set_title("SAU — trùng nhau ⇒ xám, lệch ⇒ viền màu", fontsize=8)
        else:
            ax2.text(0.5, 0.5, "Không có ma trận", ha="center", va="center")
        ax2.axis("off")
        self.fig_match.tight_layout()
        self.canvas_match.draw()

    def _report(self, r):
        self.say("")
        self.say("=== Attempts (primary + structural bootstrap) ===")
        for attempt in r.get("attempts", []):
            self.say("  [%s] geometry_valid=%s failure_reason=%s"
                     % (attempt.get("source"), attempt.get("geometry_valid"),
                        attempt.get("failure_reason")))
            for lv in attempt.get("levels", []):
                corr = lv["correlation"]
                self.say("      level %d  size=%sx%s  scale=%.4f  corr=%s"
                         % (lv["level"], lv["size"][0], lv["size"][1], lv["scale"],
                            "n/a" if corr is None else "%.5f" % corr))
        self.say("")
        if r.get("matrix") is None:
            self.say("THẤT BẠI: %s — %s" % (r["failure_reason"], r["message"]))
            return
        m = r["matrix"]
        self.say("Ma trận MovingImage -> ReferenceImage (nguồn=%s):" % r.get("source"))
        for i in range(3):
            self.say("   [ %12.6f  %12.6f  %12.4f ]" % (m[i, 0], m[i, 1], m[i, 2]))
        self.say("")
        self.say("  translation = (%.3f, %.3f) px" % (r["translation_x"], r["translation_y"]))
        if r.get("affine_normalize") is not None:
            self.say("  raw scale   = (X=%.8f, Y=%.8f)  normalize=%s"
                     % (r["raw_scale_x"], r["raw_scale_y"], r["affine_normalize"]))
        clamp_note = "  [CLAMPED]" if r.get("rotation_clamped") else ""
        self.say("  raw rotation= %.5f deg" % r.get("raw_rotation_deg", r["rotation_deg"]))
        self.say("  rotation    = %.5f deg%s" % (r["rotation_deg"], clamp_note))
        self.say("  scale       = %.8f" % r["scale"])
        self.say("  rawScore    = %.5f   (MinCorrelation %.3f)"
                 % (r["raw_score"], self.current_cfg()["EccMinCorrelation"]))
        self.say("")
        self.say("=== Xác minh cấu trúc độc lập (không dùng lại objective ECC) ===")
        self.say("  symmetric_edge_coverage = %s" % r.get("symmetric_edge_coverage"))
        self.say("  symmetric_chamfer_p95   = %s" % r.get("symmetric_chamfer_p95"))
        runner_up = r.get("runner_up")
        if runner_up is not None:
            self.say("  runner-up: translation=(%.3f, %.3f) coverage=%s margin=%s"
                     % (runner_up.get("translation_x"), runner_up.get("translation_y"),
                        runner_up.get("symmetric_edge_coverage"), r.get("coverage_margin")))
        self.say("")
        self.say("KẾT LUẬN: %s — %s: %s"
                 % (r.get("verification_status"), r.get("failure_reason"), r.get("message")))


def main():
    root = tk.Tk()
    # Co theo man hinh that thay vi hardcode 1400x900 (co the lon hon man hinh laptop/hi-DPI thu
    # nho). Du man hinh nho hon noi dung, khung cuon o ca 2 ben (App.__init__) van dam bao khong
    # bi cat mat noi dung -- chi can keo scrollbar.
    sw = root.winfo_screenwidth()
    sh = root.winfo_screenheight()
    w = min(1400, int(sw * 0.9))
    h = min(900, int(sh * 0.85))
    root.geometry("%dx%d+%d+%d" % (w, h, (sw - w) // 2, max(0, (sh - h) // 3)))
    root.minsize(860, 520)
    App(root)
    root.mainloop()


if __name__ == "__main__":
    main()
