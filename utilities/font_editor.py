#!/usr/bin/env python3
"""
DCS Font Editor
A simple interactive GUI to edit WinWing CDU font JSON files.

Features:
- Load/Save 21x31 font JSON files.
- Support for LargeGlyphs and SmallGlyphs.
- Interactive grid for editing characters.
- Drag-to-paint support.
- Clear and Invert tools.
"""

import tkinter as tk
from tkinter import filedialog, messagebox, ttk
import json
import os

class FontEditor:
    def __init__(self, root):
        self.root = root
        self.root.title("DCS Font Editor (21x31)")
        self.root.geometry("850x780")
        
        self.font_data = None
        self.file_path = None
        self.current_glyph_type = "LargeGlyphs"
        self.current_char_index = -1
        self.grid_width = 21
        self.grid_height = 31
        self.cell_size = 18
        
        self.setup_ui()
        
    def setup_ui(self):
        # Top Frame
        top_frame = tk.Frame(self.root)
        top_frame.pack(side=tk.TOP, fill=tk.X, padx=10, pady=10)
        
        tk.Button(top_frame, text="Open Font JSON", command=self.open_file, bg="#e1e1e1", width=15).pack(side=tk.LEFT, padx=5)
        tk.Button(top_frame, text="Save Font JSON", command=self.save_file, bg="#d4edda", width=15).pack(side=tk.LEFT, padx=5)
        
        self.filename_label = tk.Label(top_frame, text="No file loaded", fg="#666666", font=("Arial", 10, "italic"))
        self.filename_label.pack(side=tk.LEFT, padx=15)
        
        # Main Layout
        content_frame = tk.Frame(self.root)
        content_frame.pack(side=tk.TOP, fill=tk.BOTH, expand=True, padx=10, pady=5)
        
        # Left Panel: Character List
        left_panel = tk.Frame(content_frame)
        left_panel.pack(side=tk.LEFT, fill=tk.Y)
        
        tk.Label(left_panel, text="Glyph Type:").pack(anchor=tk.W)
        self.glyph_type_var = tk.StringVar(value="LargeGlyphs")
        self.type_combo = ttk.Combobox(left_panel, textvariable=self.glyph_type_var, values=["LargeGlyphs", "SmallGlyphs"], state="readonly", width=15)
        self.type_combo.pack(pady=(0, 10))
        self.type_combo.bind("<<ComboboxSelected>>", self.on_type_change)
        
        tk.Label(left_panel, text="Characters:").pack(anchor=tk.W)
        list_container = tk.Frame(left_panel)
        list_container.pack(fill=tk.BOTH, expand=True)
        
        self.char_listbox = tk.Listbox(list_container, width=15, font=("Courier New", 11))
        self.char_listbox.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.char_listbox.bind('<<ListboxSelect>>', self.on_char_select)
        
        scrollbar = tk.Scrollbar(list_container, orient=tk.VERTICAL)
        scrollbar.config(command=self.char_listbox.yview)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)
        self.char_listbox.config(yscrollcommand=scrollbar.set)
        
        # Right Panel: Editor
        right_panel = tk.Frame(content_frame)
        right_panel.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(20, 0))
        
        # Toolbar
        tool_frame = tk.Frame(right_panel)
        tool_frame.pack(side=tk.TOP, fill=tk.X, pady=(0, 10))
        
        tk.Button(tool_frame, text="Clear", command=self.clear_grid, width=8).pack(side=tk.LEFT, padx=2)
        tk.Button(tool_frame, text="Invert", command=self.invert_grid, width=8).pack(side=tk.LEFT, padx=2)
        
        self.char_display_label = tk.Label(tool_frame, text="Editing: -", font=("Arial", 12, "bold"))
        self.char_display_label.pack(side=tk.LEFT, padx=20)
        
        # Canvas for Grid
        self.canvas_frame = tk.Frame(right_panel, bd=1, relief=tk.SOLID)
        self.canvas_frame.pack(side=tk.TOP)
        
        self.canvas = tk.Canvas(self.canvas_frame, 
                                width=self.grid_width * self.cell_size, 
                                height=self.grid_height * self.cell_size, 
                                bg="#CCCCCC", highlightthickness=0)
        self.canvas.pack()
        self.canvas.bind("<Button-1>", self.on_canvas_click)
        self.canvas.bind("<B1-Motion>", self.on_canvas_drag)
        
        # Legend
        legend_frame = tk.Frame(right_panel)
        legend_frame.pack(side=tk.TOP, fill=tk.X, pady=10)
        tk.Label(legend_frame, text="Left Click: Toggle | Click & Drag: Paint", fg="#555555").pack(side=tk.LEFT)
        
        # Footer / Status
        self.status_bar = tk.Label(self.root, text="Ready", bd=1, relief=tk.SUNKEN, anchor=tk.W)
        self.status_bar.pack(side=tk.BOTTOM, fill=tk.X)

    def open_file(self):
        initial_dir = os.path.join(os.getcwd(), "Resources")
        if not os.path.exists(initial_dir):
            initial_dir = os.getcwd()
            
        file_path = filedialog.askopenfilename(initialdir=initial_dir, filetypes=[("JSON files", "*.json"), ("All files", "*.*")])
        if not file_path:
            return
            
        try:
            with open(file_path, 'r', encoding='utf-8') as f:
                self.font_data = json.load(f)
            
            self.file_path = file_path
            self.filename_label.config(text=os.path.basename(file_path), fg="blue")
            
            # Update Grid Dimensions from file if they exist
            self.grid_width = self.font_data.get("GlyphWidth", 21)
            self.grid_height = self.font_data.get("GlyphHeight", 31)
            
            # Refresh UI Canvas size
            self.canvas.config(width=self.grid_width * self.cell_size, height=self.grid_height * self.cell_size)
            
            types = []
            if "LargeGlyphs" in self.font_data: types.append("LargeGlyphs")
            if "SmallGlyphs" in self.font_data: types.append("SmallGlyphs")
            
            if types:
                self.type_combo.config(values=types)
                if self.current_glyph_type not in types:
                    self.current_glyph_type = types[0]
                    self.glyph_type_var.set(self.current_glyph_type)
            
            self.update_char_list()
            self.set_status(f"Loaded {os.path.basename(file_path)}")
        except Exception as e:
            messagebox.showerror("Error", f"Failed to load font: {e}")

    def set_status(self, text):
        self.status_bar.config(text=text)

    def on_type_change(self, event):
        self.current_glyph_type = self.glyph_type_var.get()
        self.update_char_list()
        self.current_char_index = -1
        self.draw_grid()

    def update_char_list(self):
        self.char_listbox.delete(0, tk.END)
        if not self.font_data or self.current_glyph_type not in self.font_data:
            return
            
        for glyph in self.font_data[self.current_glyph_type]:
            char = glyph.get("Character", "?")
            display_name = char
            if char == " ": display_name = "[SPACE]"
            elif char == "": display_name = "[EMPTY]"
            self.char_listbox.insert(tk.END, display_name)
            
    def on_char_select(self, event):
        selection = self.char_listbox.curselection()
        if not selection:
            return
            
        self.current_char_index = selection[0]
        char = self.font_data[self.current_glyph_type][self.current_char_index].get("Character", "")
        if char == " ": char = "SPACE"
        self.char_display_label.config(text=f"Editing: '{char}'")
        self.draw_grid()
        
    def draw_grid(self):
        self.canvas.delete("all")
        if self.current_char_index < 0:
            return
            
        glyph = self.font_data[self.current_glyph_type][self.current_char_index]
        bit_array = glyph.get("BitArray", [])
        
        for r in range(self.grid_height):
            line = bit_array[r] if r < len(bit_array) else "." * self.grid_width
            for c in range(self.grid_width):
                char = line[c] if c < len(line) else "."
                color = "black" if char == 'X' else "white"
                
                x1 = c * self.cell_size
                y1 = r * self.cell_size
                x2 = x1 + self.cell_size
                y2 = y1 + self.cell_size
                
                self.canvas.create_rectangle(x1, y1, x2, y2, fill=color, outline="#DDDDDD", tags=f"cell_{r}_{c}")

    def on_canvas_click(self, event):
        self.last_drag_cell = None
        self.toggle_cell(event.x, event.y, mode="toggle")

    def on_canvas_drag(self, event):
        self.toggle_cell(event.x, event.y, mode="paint")

    def toggle_cell(self, x, y, mode="toggle"):
        if self.current_char_index < 0:
            return
            
        col = int(x // self.cell_size)
        row = int(y // self.cell_size)
        
        if 0 <= col < self.grid_width and 0 <= row < self.grid_height:
            # Avoid re-toggling the same cell during a single drag operation
            if hasattr(self, 'last_drag_cell') and mode == "paint" and self.last_drag_cell == (row, col):
                return
            
            glyph = self.font_data[self.current_glyph_type][self.current_char_index]
            bit_array = glyph.get("BitArray", [])
            
            # Pad bit_array if it's shorter than grid_height
            while len(bit_array) < self.grid_height:
                bit_array.append("." * self.grid_width)
            
            line = list(bit_array[row])
            # Pad line if it's shorter than grid_width
            while len(line) < self.grid_width:
                line.append(".")
            
            if mode == "toggle":
                line[col] = 'X' if line[col] == '.' else '.'
                self.paint_val = line[col]
            else: # paint mode
                if hasattr(self, 'paint_val'):
                    line[col] = self.paint_val
                else:
                    line[col] = 'X'
            
            bit_array[row] = "".join(line)
            glyph["BitArray"] = bit_array
            
            self.last_drag_cell = (row, col)
            
            color = "black" if line[col] == 'X' else "white"
            self.canvas.itemconfig(f"cell_{row}_{col}", fill=color)

    def clear_grid(self):
        if self.current_char_index < 0: return
        if not messagebox.askyesno("Confirm", "Are you sure you want to clear this glyph?"): return
        glyph = self.font_data[self.current_glyph_type][self.current_char_index]
        glyph["BitArray"] = ["." * self.grid_width for _ in range(self.grid_height)]
        self.draw_grid()

    def invert_grid(self):
        if self.current_char_index < 0: return
        glyph = self.font_data[self.current_glyph_type][self.current_char_index]
        bit_array = glyph.get("BitArray", [])
        new_bit_array = []
        for row in bit_array:
            # Handle potential mismatch in lengths
            while len(row) < self.grid_width: row += "."
            new_row = "".join(['.' if c == 'X' else 'X' for c in row[:self.grid_width]])
            new_bit_array.append(new_row)
        
        # Pad to full height if necessary
        while len(new_bit_array) < self.grid_height:
            new_bit_array.append("X" * self.grid_width)
            
        glyph["BitArray"] = new_bit_array
        self.draw_grid()

    def save_file(self):
        if not self.font_data or not self.file_path:
            messagebox.showwarning("Warning", "No font data to save.")
            return
            
        try:
            with open(self.file_path, 'w', encoding='utf-8') as f:
                json.dump(self.font_data, f, indent=2, ensure_ascii=False)
            self.set_status(f"Successfully saved to {os.path.basename(self.file_path)}")
            messagebox.showinfo("Success", f"Font saved to {self.file_path}")
        except Exception as e:
            messagebox.showerror("Error", f"Failed to save font: {e}")

if __name__ == "__main__":
    root = tk.Tk()
    
    # Try to make it look a bit modern
    try:
        style = ttk.Style()
        if os.name == 'nt':
            style.theme_use('vista')
        else:
            style.theme_use('clam')
    except:
        pass
        
    app = FontEditor(root)
    root.mainloop()
