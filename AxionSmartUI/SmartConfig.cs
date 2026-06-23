﻿using System.Collections.Generic;

namespace AxionSmartUI
{
    public class SmartConfig
    {
        //NORMAL
        public double NormalVolume { get; set; } = 50;
        public byte NormalBrightness { get; set; } = 60;

        //FULLSCREEN
        public double FullscreenVolume { get; set; } = 80;
        public byte FullscreenBrightness { get; set; } = 100;

        //NIGHT 
        public double NightVolume { get; set; } = 20;
        public byte NightBrightness { get; set; } = 25;

        //SISTEMA 
        public bool StartWithWindows { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public bool AutoMode { get; set; } = true;

        //AUTOMATION LAS QUE FALTABAN
        public bool EnableFullscreenMode { get; set; } = false;
        public bool EnableNightMode { get; set; } = false;
        public bool EnableDiscordMode { get; set; } = false;
        public bool EnableBlueLightFilter { get; set; } = false; // Nuevo: para el filtro de luz azul

        //PERFILES Y APPS
        public List<Profile> Profiles { get; set; } = new();
        // Lista de ejecutables
        public List<string> CustomApps { get; set; } = new() { "vlc", "obs64" };
    }
}