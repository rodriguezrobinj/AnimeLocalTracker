import React from 'react';
import { LayoutGrid, CalendarClock, Globe, Server, BrainCircuit } from 'lucide-react';

interface SidebarProps {
  activeView: string;
  onNavigate: (view: string) => void;
}

export function Sidebar({ activeView, onNavigate }: SidebarProps) {
  const navItems = [
    { id: 'gallery', icon: LayoutGrid, tooltip: 'Biblioteca' },
    { id: 'calendar', icon: CalendarClock, tooltip: 'Calendario de Emisión' },
    { id: 'web', icon: Globe, tooltip: 'Nueva Interfaz Web', color: 'text-pink-500' },
    { id: 'go', icon: Server, tooltip: 'Ping al Servidor Go', color: 'text-cyan-400' },
    { id: 'python', icon: BrainCircuit, tooltip: 'Ping al Motor Python (IA)', color: 'text-yellow-400' },
  ];

  return (
    <div className="w-[65px] h-full border-r border-white/10 flex flex-col items-center pt-[60px] bg-background z-10 shrink-0">
      {navItems.map((item) => {
        const Icon = item.icon;
        const isActive = activeView === item.id;
        return (
          <button
            key={item.id}
            onClick={() => onNavigate(item.id)}
            title={item.tooltip}
            className={`w-[45px] h-[45px] rounded-full flex items-center justify-center mb-5 transition-all duration-200
              ${isActive ? 'bg-surfaceLight' : 'hover:bg-surface'}
            `}
          >
            <Icon 
              size={24} 
              className={item.color ? item.color : (isActive ? 'text-textMain' : 'text-textMuted')} 
            />
          </button>
        );
      })}
    </div>
  );
}
