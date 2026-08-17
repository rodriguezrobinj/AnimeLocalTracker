import React, { useState, useEffect } from 'react';
import { MainLayout } from './layouts/MainLayout';
import { GalleryView } from './views/GalleryView';
import { DetalleView } from './views/DetalleView';
import { CalendarView } from './views/CalendarView';
import './index.css';

function App() {
  const [activeView, setActiveView] = useState('gallery');
  const [selectedAnime, setSelectedAnime] = useState<any>(null);
  
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const [messages, setMessages] = useState<string[]>([]);

  useEffect(() => {
    // Escuchar mensajes provenientes de C# (WebView2)
    const handleMessage = (event: MessageEvent) => {
      try {
        const data = JSON.parse(event.data);
        if (data.type === 'Navigation') {
          setActiveView(data.target);
        } else {
          setMessages(prev => [...prev, `[C# JSON]: ${event.data}`]);
        }
      } catch {
        setMessages(prev => [...prev, `[C# String]: ${event.data}`]);
      }
    };

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const webViewObj = (window as any).chrome?.webview;
    if (webViewObj) {
      webViewObj.addEventListener('message', handleMessage);
    }

    return () => {
      if (webViewObj) {
        webViewObj.removeEventListener('message', handleMessage);
      }
    };
  }, []);

  const handleAnimeClick = (anime: any) => {
    setSelectedAnime(anime);
    setActiveView('detail');
  };

  return (
    <MainLayout activeView={activeView} onNavigate={setActiveView}>
      {activeView === 'gallery' && <GalleryView onAnimeClick={handleAnimeClick} />}
      {activeView === 'detail' && selectedAnime && (
        <DetalleView 
          anime={selectedAnime} 
          onBack={() => setActiveView('gallery')} 
        />
      )}
      {activeView === 'calendar' && <CalendarView />}
      {activeView === 'web' && <div className="p-8 text-pink-500">Vista Web</div>}
      {activeView === 'go' && <div className="p-8 text-cyan-400">Ping Go UI</div>}
      {activeView === 'python' && <div className="p-8 text-yellow-400">Ping Python UI</div>}
    </MainLayout>
  );
}

export default App;
