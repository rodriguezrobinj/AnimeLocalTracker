import React, { useState } from 'react';
import { Sidebar } from '../components/Sidebar';

interface MainLayoutProps {
  children?: React.ReactNode;
  activeView: string;
  onNavigate: (view: string) => void;
}

export function MainLayout({ children, activeView, onNavigate }: MainLayoutProps) {
  const handleNavigate = (view: string) => {
    onNavigate(view);
    
    // Opcional: avisar a C# que cambiamos de vista si lo necesita
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const webViewObj = (window as any).chrome?.webview;
    if (webViewObj) {
      webViewObj.postMessage(JSON.stringify({ action: 'Navigation', target: view }));
    }
  };

  return (
    <div className="w-screen h-screen bg-background text-textMain flex overflow-hidden font-sans">
      <Sidebar activeView={activeView} onNavigate={handleNavigate} />
      
      {/* El contenido principal que ocupa el resto del espacio */}
      <div className="flex-1 flex flex-col h-full relative overflow-hidden">
        {/* Placeholder padding para la barra de título nativa (45px) */}
        <div className="h-[45px] w-full shrink-0" />
        
        <div className="flex-1 overflow-y-auto overflow-x-hidden">
           {children}
        </div>
      </div>
    </div>
  );
}
