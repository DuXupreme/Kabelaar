"""Lichtgewicht tooltip voor Tk/ttk-widgets.

Tkinter heeft geen ingebouwde tooltip. Deze helper toont na een korte
vertraging een klein zwevend label bij hover en ruimt het weer op.
"""

from __future__ import annotations

import tkinter as tk
from tkinter import font as tkfont

import theme as ui_theme


class Tooltip:
    def __init__(self, widget, text: str, delay: int = 500):
        self.widget = widget
        self.text = text
        self.delay = delay
        self._after_id = None
        self._tip = None
        widget.bind("<Enter>", self._schedule, add="+")
        widget.bind("<Leave>", self._hide, add="+")
        widget.bind("<ButtonPress>", self._hide, add="+")

    def _schedule(self, _event=None):
        self._cancel()
        try:
            self._after_id = self.widget.after(self.delay, self._show)
        except tk.TclError:
            self._after_id = None

    def _cancel(self):
        if self._after_id is not None:
            try:
                self.widget.after_cancel(self._after_id)
            except tk.TclError:
                pass
            self._after_id = None

    def _show(self):
        if self._tip is not None or not self.text:
            return
        try:
            x = self.widget.winfo_rootx() + 12
            y = self.widget.winfo_rooty() + self.widget.winfo_height() + 6
        except tk.TclError:
            return
        self._tip = tw = tk.Toplevel(self.widget)
        tw.wm_overrideredirect(True)
        tw.wm_geometry(f"+{x}+{y}")
        # Warme inkt-chip; dezelfde tint werkt in licht- en donkermodus.
        chip = ui_theme.LIGHT
        try:
            tip_font = tkfont.nametofont("TkTooltipFont")
        except tk.TclError:
            tip_font = None
        label = tk.Label(
            tw,
            text=self.text,
            justify="left",
            background=chip["tooltip_bg"],
            foreground=chip["tooltip_fg"],
            relief="flat",
            borderwidth=0,
            padx=10,
            pady=6,
            font=tip_font,
        )
        label.pack()

    def _hide(self, _event=None):
        self._cancel()
        if self._tip is not None:
            try:
                self._tip.destroy()
            except tk.TclError:
                pass
            self._tip = None


def attach(widget, text: str) -> Tooltip:
    """Koppel een tooltip aan een widget en geef het Tooltip-object terug."""
    return Tooltip(widget, text)
