import React, { useState, useEffect } from 'react';
import { Search, Check, RefreshCw } from 'lucide-react';

export function GalleryView({ onAnimeClick }: { onAnimeClick: (anime: any) => void }) {
  const tabs = ['Todos', 'Viendo', 'Completados', 'Planeando'];
  
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const [animes, setAnimes] = useState<any[]>([]);

  useEffect(() => {
    const handleMessage = (event: MessageEvent) => {
      try {
        const payload = JSON.parse(event.data);
        if (payload.type === 'UpdateGallery') {
          setAnimes(payload.data);
        }
      } catch (e) {
        // Ignorar
      }
    };

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const webViewObj = (window as any).chrome?.webview;
    if (webViewObj) {
      webViewObj.addEventListener('message', handleMessage);
      webViewObj.postMessage('ReactReady');
    }

    return () => {
      if (webViewObj) {
        webViewObj.removeEventListener('message', handleMessage);
      }
    };
  }, []);

  const [activeTab, setActiveTab] = useState('Todos');
  const [searchTerm, setSearchTerm] = useState('');

  const filteredAnimes = animes.filter(anime => {
    // Tab Filter
    if (activeTab === 'Viendo' && anime.estadoUsuario !== 'CURRENT') return false;
    if (activeTab === 'Completados' && anime.estadoUsuario !== 'COMPLETED') return false;
    if (activeTab === 'Planeando' && anime.estadoUsuario !== 'PLANNING') return false;
    
    // Search Filter
    if (searchTerm && !anime.titulo.toLowerCase().includes(searchTerm.toLowerCase())) return false;
    
    return true;
  });

  const [isSyncing, setIsSyncing] = useState(false);

  useEffect(() => {
    // Escuchar mensajes para detener la animación de carga
    const handleMessage = (event: MessageEvent) => {
      try {
        const payload = JSON.parse(event.data);
        if (payload.type === 'SyncComplete') {
          setIsSyncing(false);
          // Pedir a C# que recargue la galería para ver los nuevos animes:
          const webViewObj = (window as any).chrome?.webview;
          if (webViewObj) webViewObj.postMessage('ReactReady');
        }
      } catch (e) {
        // Ignorar
      }
    };

    const webViewObj = (window as any).chrome?.webview;
    if (webViewObj) {
      webViewObj.addEventListener('message', handleMessage);
    }
    return () => {
      if (webViewObj) webViewObj.removeEventListener('message', handleMessage);
    };
  }, []);

  const handleSync = () => {
    setIsSyncing(true);
    const webViewObj = (window as any).chrome?.webview;
    if (webViewObj) {
      webViewObj.postMessage(JSON.stringify({ action: 'Sincronizar' }));
    } else {
      setTimeout(() => setIsSyncing(false), 2000);
    }
  };

  return (
    <div className="p-8 w-full h-full flex flex-col">
      {/* Header */}
      <div className="flex justify-between items-center mb-8">
        <h1 className="text-4xl font-extrabold text-white tracking-tight">Mi Colección</h1>
        <div className="flex items-center space-x-4">
          <button className="flex items-center space-x-2 bg-transparent border border-primary text-primary hover:bg-primary/10 px-4 py-2 rounded-md transition-colors font-medium text-sm">
            <Search size={16} />
            <span>BUSCAR ANIME</span>
          </button>
          
          <button className="bg-surfaceLight hover:bg-white/10 p-2 rounded-full text-textMuted hover:text-white transition-colors">
            <Check size={20} />
          </button>
          
          <div className="flex items-center bg-surfaceLight px-3 py-1.5 rounded-full space-x-2">
            <div className="w-6 h-6 rounded-full bg-blue-500"></div>
            <span className="text-sm font-medium">rodriguezrobinj</span>
          </div>
          
          <button 
            onClick={handleSync}
            disabled={isSyncing}
            className={`flex items-center space-x-2 text-white px-4 py-2 rounded-md transition-colors font-medium text-sm
              ${isSyncing ? 'bg-green-600/50 cursor-not-allowed' : 'bg-green-500 hover:bg-green-600'}
            `}
          >
            <RefreshCw size={16} className={isSyncing ? 'animate-spin' : ''} />
            <span>{isSyncing ? 'SINCRONIZANDO...' : 'SINCRONIZAR'}</span>
          </button>
        </div>
      </div>
      
      {/* Search Input & Tabs */}
      <div className="flex justify-between items-center mb-8">
        <div className="relative w-96">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 text-textMuted" size={18} />
          <input 
            type="text" 
            placeholder="Buscar anime guardado..." 
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="w-full bg-surfaceLight text-textMain pl-10 pr-4 py-2 rounded-lg border-none focus:ring-2 focus:ring-primary outline-none"
          />
        </div>
        
        <div className="flex space-x-6 border-b border-white/10">
          {tabs.map((tab) => (
            <button 
              key={tab} 
              onClick={() => setActiveTab(tab)}
              className={`pb-2 px-1 text-sm font-medium transition-colors ${activeTab === tab ? 'text-green-400 border-b-2 border-green-400' : 'text-textMuted hover:text-textMain'}`}
            >
              {tab}
            </button>
          ))}
        </div>
      </div>

      {/* Grid */}
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-6 overflow-y-auto pb-10 pr-2">
        {filteredAnimes.map(anime => (
          <div key={anime.aniListId} className="flex flex-col w-full" onClick={() => onAnimeClick(anime)}>
            <div 
              className="relative w-full rounded-xl overflow-hidden group cursor-pointer shadow-lg hover:shadow-primary/20 transition-all duration-300 transform hover:-translate-y-1 bg-surfaceLight"
              style={{ paddingBottom: '150%' }}
            >
              <img src={anime.urlPortada} alt={anime.titulo} className="absolute inset-0 w-full h-full object-cover" />
              
              {/* Gradient Overlay */}
              <div className="absolute inset-0 bg-gradient-to-t from-black/90 via-black/30 to-transparent"></div>
              
              {/* Status Badge */}
              <div className={`absolute top-3 left-3 px-2 py-0.5 rounded text-xs font-bold text-white shadow-md
                ${anime.estado === 'RELEASING' ? 'bg-green-500' : 'bg-gray-500/80 backdrop-blur-sm'}
              `}>
                {anime.estado === 'RELEASING' ? 'En Emisión' : 'Finalizado'}
              </div>
              
              {/* Info bottom */}
              <div className="absolute bottom-0 left-0 w-full p-4">
                <h3 className="text-white font-bold text-lg leading-tight mb-1 truncate">{anime.titulo}</h3>
                <p className="text-gray-300 text-xs font-medium">{anime.episodiosVistos} de {anime.totalEpisodios || '?'} vistos</p>
                {/* Progress bar */}
                <div className="w-full h-1 bg-white/20 mt-2 rounded-full overflow-hidden">
                  <div className={`h-full transition-all duration-500 ${anime.episodiosVistos === anime.totalEpisodios && anime.totalEpisodios > 0 ? 'bg-red-500' : 'bg-white'}`} style={{ width: anime.totalEpisodios > 0 ? `${(anime.episodiosVistos / anime.totalEpisodios) * 100}%` : '0%' }}></div>
                </div>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
