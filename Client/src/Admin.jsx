import { useState, useEffect } from 'react';
import axios from 'axios';
import { Lock, Upload, FileSpreadsheet, Check, Download, RefreshCw, Calendar, FileText, Eye } from 'lucide-react';

export default function Admin() {
  const [isLoggedIn, setIsLoggedIn] = useState(false);
  const [password, setPassword] = useState('');
  const [uploading, setUploading] = useState(false);
  const [msg, setMsg] = useState('');
  const [currentFile, setCurrentFile] = useState(null);

  // פונקציה לטעינת מידע על הקובץ הקיים בשרת
  const fetchFileInfo = async () => {
    try {
      const res = await axios.get('http://localhost:5219/api/admin/current-file-info');
      if (res.data.exists) {
        setCurrentFile(res.data);
      } else {
        setCurrentFile(null);
      }
    } catch (e) { 
      console.error("Error fetching file info:", e); 
    }
  };

  // טעינת המידע מיד לאחר כניסה
  useEffect(() => {
    if (isLoggedIn) {
        fetchFileInfo();
    }
  }, [isLoggedIn]);

  const handleLogin = async () => {
    try {
      await axios.post('http://localhost:5219/api/admin/login', { password });
      setIsLoggedIn(true);
    } catch (e) {
      alert('סיסמה שגויה');
    }
  };

  const handleFileUpload = async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    
    if (!confirm("שימי לב: פעולה זו תבצע סנכרון מלא.\nחובות שאינם מופיעים בקובץ החדש יוסרו מהתצוגה בבוט.\nהאם להמשיך?")) return;

    setUploading(true);
    setMsg('');
    
    const formData = new FormData();
    formData.append('file', file);

    try {
      const res = await axios.post('http://localhost:5219/api/admin/upload-excel', formData);
      setMsg(res.data.message);
      // רענון המידע על הקובץ מיד אחרי ההעלאה
      await fetchFileInfo(); 
    } catch (error) {
      alert('שגיאה בהעלאה: ' + (error.response?.data || error.message));
    } finally {
      setUploading(false);
    }
  };

  // פונקציה להורדת הקובץ הקיים
  const downloadCurrent = () => {
      // פתיחת הקישור בחלון חדש מורידה את הקובץ
      window.open('http://localhost:5219/api/admin/download-current', '_blank');
  };

  const handleDownloadReport = async () => {
    try {
      const response = await axios.get('http://localhost:5219/api/admin/export-submissions', {
        responseType: 'blob',
      });
      const url = window.URL.createObjectURL(new Blob([response.data]));
      const link = document.createElement('a');
      link.href = url;
      link.setAttribute('download', 'SubmissionsReport.xlsx');
      document.body.appendChild(link);
      link.click();
      link.remove();
    } catch (error) { alert('שגיאה בהורדת הדוח'); }
  };

  // --- מסך התחברות ---
  if (!isLoggedIn) {
    return (
      <div className="flex h-screen items-center justify-center bg-gray-100" dir="rtl">
        <div className="bg-white p-8 rounded-xl shadow-lg w-96 text-center border border-gray-200">
          <div className="bg-blue-100 p-4 rounded-full w-fit mx-auto mb-4 text-blue-600">
            <Lock size={32} />
          </div>
          <h2 className="text-2xl font-bold mb-4 text-gray-800">כניסת הנהלה</h2>
          <input 
            type="password" 
            placeholder="סיסמה" 
            className="w-full p-3 border rounded-lg mb-4 text-center focus:ring-2 focus:ring-blue-500 outline-none"
            value={password}
            onChange={e => setPassword(e.target.value)}
          />
          <button onClick={handleLogin} className="w-full bg-blue-600 text-white p-3 rounded-lg hover:bg-blue-700 font-bold shadow transition">
            כניסה למערכת
          </button>
        </div>
      </div>
    );
  }

  // --- מסך ניהול ---
  return (
    <div className="p-8 bg-gray-50 min-h-screen font-sans" dir="rtl">
      <div className="max-w-4xl mx-auto">
        <div className="flex justify-between items-center mb-8 pb-4 border-b border-gray-200">
            <h1 className="text-3xl font-black text-gray-800 flex items-center gap-3">
                <Lock className="text-blue-600"/> ממשק ניהול - בוט השלמות
            </h1>
            <button 
                onClick={() => setIsLoggedIn(false)} 
                className="text-red-500 hover:bg-red-50 px-4 py-2 rounded-lg font-bold text-sm transition"
            >
                יציאה
            </button>
        </div>
        
        <div className="grid gap-6">
            {/* 1. כרטיס קובץ נתונים נוכחי (התווסף!) */}
            <div className="bg-white p-6 rounded-2xl shadow-sm border border-blue-100">
                <h3 className="text-xl font-bold mb-4 text-gray-800 flex items-center gap-2">
                    <FileText className="text-blue-600"/> קובץ נתונים פעיל
                </h3>
                
                {currentFile ? (
                    <div className="flex flex-col sm:flex-row sm:items-center justify-between bg-blue-50 p-4 rounded-xl border border-blue-200 gap-4">
                        <div>
                            <div className="font-bold text-blue-900 text-lg flex items-center gap-2">
                                <Check className="text-green-600" size={20}/>
                                {currentFile.fileName}
                            </div>
                            <div className="text-sm text-blue-700 flex items-center gap-1 mt-1">
                                <Calendar size={14}/> תאריך העלאה: {new Date(currentFile.lastModified).toLocaleString()}
                            </div>
                        </div>
                        
                        {/* כפתור הצפייה שביקשת */}
                        <button 
                            onClick={downloadCurrent} 
                            className="bg-white text-blue-700 px-5 py-2.5 rounded-lg border border-blue-200 hover:bg-blue-600 hover:text-white font-bold shadow-sm flex items-center gap-2 transition"
                        >
                            <Eye size={18}/> צפייה / הורדת הקובץ
                        </button>
                    </div>
                ) : (
                    <div className="text-gray-500 italic p-6 bg-gray-50 rounded-xl text-center border border-dashed border-gray-300">
                        <div className="mb-2">לא נמצא קובץ נתונים בשרת.</div>
                        <div className="text-sm">יש להעלות קובץ חדש כדי שיופיע כאן.</div>
                    </div>
                )}
            </div>

            {/* 2. כרטיס העלאה וסנכרון */}
            <div className="bg-white p-6 rounded-2xl shadow-sm border border-gray-200">
                <h3 className="text-xl font-bold mb-2 text-gray-800 flex items-center gap-2">
                    <RefreshCw className="text-orange-500"/>
                    העלאת קובץ חדש (עדכון)
                </h3>
                <p className="text-gray-500 mb-6 text-sm">
                    העלאת קובץ תחליף את הקובץ הישן ותבצע <b>סנכרון מלא</b> של החובות במערכת.
                </p>

                <label className={`block w-full border-2 border-dashed ${uploading ? 'border-blue-300 bg-blue-50' : 'border-gray-300 hover:border-blue-400 hover:bg-gray-50'} rounded-xl p-10 text-center cursor-pointer transition duration-300 group`}>
                    <input type="file" className="hidden" accept=".xlsx" onChange={handleFileUpload} disabled={uploading} />
                    {uploading ? (
                        <div className="flex flex-col items-center gap-2">
                            <Loader2 className="animate-spin text-blue-600" size={40}/>
                            <span className="text-blue-600 font-bold">מעבד נתונים ומסנכרן... נא להמתין</span>
                        </div>
                    ) : (
                        <div className="flex flex-col items-center gap-3 text-gray-500 group-hover:text-blue-600">
                            <div className="bg-blue-100 p-4 rounded-full text-blue-600 group-hover:bg-blue-200 transition">
                                <Upload size={32} />
                            </div>
                            <div>
                                <span className="font-bold text-lg block text-gray-800">לחצי כאן לבחירת קובץ Excel לעדכון</span>
                                <span className="text-sm opacity-70">סיומת xlsx בלבד</span>
                            </div>
                        </div>
                    )}
                </label>

                {msg && (
                    <div className="mt-6 p-4 bg-green-50 text-green-800 rounded-xl flex items-center gap-3 border border-green-200 animate-in fade-in slide-in-from-bottom-2">
                        <div className="bg-green-200 p-1 rounded-full"><Check size={16}/></div>
                        {msg}
                    </div>
                )}
            </div>

            {/* 3. כרטיס דוחות */}
            <div className="bg-white p-6 rounded-2xl shadow-sm border border-gray-200">
                <h3 className="text-xl font-bold mb-4 flex items-center gap-2">
                    <FileSpreadsheet className="text-green-600"/>
                    דוחות וסטטיסטיקה
                </h3>
                <button 
                    onClick={handleDownloadReport}
                    className="w-full sm:w-auto bg-green-600 hover:bg-green-700 text-white px-6 py-3 rounded-xl flex items-center justify-center gap-2 font-bold shadow-md transition transform active:scale-95"
                >
                    <Download size={20}/>
                    הורדת דוח הגשות (Excel)
                </button>
            </div>
        </div>
      </div>
    </div>
  );
}