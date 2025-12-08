import { useState, useRef, useEffect } from 'react';
import axios from 'axios';
import { Send, Upload, CreditCard, FileText, CheckCircle, AlertCircle, Loader2, Lock, X, Check, Clock, Ban } from 'lucide-react';
import { Link } from 'react-router-dom';

// --- פונקציות עזר ---
const getVal = (obj, key1, key2) => {
  if (!obj) return null;
  return obj[key1] !== undefined ? obj[key1] : obj[key2];
};

// --- רכיב 1: חלונית תשלום (Iframe) ---
const PaymentIframe = ({ totalAmount, student, onSuccess, onClose }) => {
  const iframeRef = useRef(null);
  const [status, setStatus] = useState('מאתחל מערכת תשלומים...');

  const initIframe = () => {
    if (iframeRef.current && iframeRef.current.contentWindow) {
      const postData = {
        Name: 'PostNedarim',
        Value: {
          Mosad: '7001475',
          ApiValid: 'MykxduB97f',
          Zeout: student.StudentID || '',
          FirstName: student.FirstName || '',
          LastName: student.LastName || '',
          ClientName: `${student.FirstName} ${student.LastName}`, 
          PaymentType: 'Ragil',
          Amount: totalAmount,
          Tashlumim: '1',
          Comment: 'תשלום עבור השלמת עבודות',
          WaitFrame: '1', 
          ForceUpdateMatching: '1',
          CallBack: '', 
          Street: 'לא הוזן', City: 'לא הוזן', Phone: '0000000000', Mail: ''
        }
      };
      console.log("Sending Data to Iframe:", postData);
      iframeRef.current.contentWindow.postMessage(postData, '*');
      setStatus('המערכת מוכנה. אנא הזיני פרטי אשראי ולחצי על אישור למטה.');
    }
  };

  const executePayment = () => {
    if (iframeRef.current && iframeRef.current.contentWindow) {
        const actionData = { Name: 'FinishTransaction' }; 
        iframeRef.current.contentWindow.postMessage(actionData, '*');
        setStatus('מבצע תשלום, נא להמתין...');
    }
  };

  const bypassPayment = () => {
      if (confirm("האם לדמות תשלום מוצלח? (מיועד לבדיקות פיתוח בלבד)")) {
          onSuccess("TEST-TRANSACTION-123");
      }
  };

  useEffect(() => {
    const handleMessage = (event) => {
      const data = event.data;
      if (!data || !data.Name) return;

      if (data.Name === 'Resize') {
        if (iframeRef.current) {
          iframeRef.current.style.height = (data.Value + 20) + 'px';
        }
        setTimeout(initIframe, 500);
      }

      if (data.Name === 'TransactionResponse') {
        if (data.Value.Status === 'OK') {
          onSuccess(data.Value.TransactionId);
        } else {
          const msg = data.Value.Message || 'שגיאה כללית';
          if (msg !== "מספר עסקה לא תקין") setStatus('התשלום נכשל: ' + msg);
        }
      }
    };

    window.addEventListener('message', handleMessage);
    const timeout = setTimeout(initIframe, 2000);
    return () => {
      window.removeEventListener('message', handleMessage);
      clearTimeout(timeout);
    };
  }, [onSuccess, totalAmount, student]);

  return (
    <div className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50 p-4" style={{direction: 'rtl', zIndex: 9999}}>
      <div className="bg-white w-full max-w-lg rounded-2xl shadow-2xl overflow-hidden flex flex-col max-h-[90vh]">
        <div className="bg-gradient-to-l from-green-600 to-teal-600 p-4 flex justify-between items-center text-white">
          <div>
            <h3 className="font-bold text-lg flex items-center gap-2"><CreditCard className="h-5 w-5"/> תשלום אשראי</h3>
            <p className="text-sm opacity-90 mt-1">עבור: <b>{student.FirstName} {student.LastName}</b> | סכום: <b>{totalAmount} ₪</b></p>
          </div>
          <button onClick={onClose} className="p-2 hover:bg-white/20 rounded-full transition"><X size={20} /></button>
        </div>
        <div className="bg-yellow-50 text-yellow-800 text-xs p-2 text-center border-b border-yellow-100">{status}</div>
        <div className="flex-1 overflow-y-auto bg-white relative p-1">
           <iframe ref={iframeRef} src="https://www.matara.pro/nedarimplus/iframe/" className="w-full border-none" style={{ minHeight: '400px', width: '100%' }} title="Payment Frame" sandbox="allow-scripts allow-same-origin allow-forms allow-popups"/>
        </div>
        <div className="p-4 border-t bg-gray-50 flex justify-center">
            <button onClick={executePayment} className="w-full bg-green-600 hover:bg-green-700 text-white font-bold py-3 px-6 rounded-xl shadow-lg transform active:scale-95 transition flex items-center justify-center gap-2"><Check size={20} /> אישור וביצוע תשלום</button>
            <button onClick={bypassPayment} className="w-full bg-gray-200 hover:bg-gray-300 text-gray-600 text-xs py-2 px-4 rounded-lg flex items-center justify-center gap-1"><AlertCircle size={14} /> עקוף תשלום (מצב פיתוח)</button>
        </div>
      </div>
    </div>
  );
};

// --- רכיב 2: רשימת החובות (הלוגיקה המרכזית) ---
const DebtsList = ({ debts, onPay, onUpload, uploadingId }) => {
  const unpaidDebts = debts.filter(d => !getVal(d, 'isPaid', 'IsPaid'));
  const totalAmount = unpaidDebts.length * 1; // מחיר: 1 ש"ח לקורס

  // חישוב עבודות המותרות להגשה (עד 300 שעות)
  let accumulatedHours = 0;
  const LIMIT = 300;
  const allowedSubmissionIds = new Set();
  
  debts.forEach(d => {
      const hours = Number(getVal(d, 'hours', 'Hours') || 0);
      if (accumulatedHours < LIMIT) {
          allowedSubmissionIds.add(getVal(d, 'debtID', 'DebtID'));
          accumulatedHours += hours;
      }
  });

  return (
    <div className="mt-4 space-y-4">
      {/* כפתור תשלום מרוכז */}
      {totalAmount > 0 ? (
           <div className="bg-orange-50 border border-orange-200 p-4 rounded-xl flex justify-between items-center shadow-sm">
               <div>
                   <span className="font-bold text-orange-800">ישנם {unpaidDebts.length} קורסים לתשלום</span>
                   <p className="text-xs text-orange-600">סה"כ לתשלום: {totalAmount} ₪ (כולל כל הקורסים)</p>
               </div>
               <button 
                  onClick={() => onPay(unpaidDebts, totalAmount)}
                  className="bg-orange-600 hover:bg-orange-700 text-white px-4 py-2 rounded-lg text-sm font-bold shadow flex items-center gap-2"
               >
                  <CreditCard size={16}/> מעבר לתשלום
               </button>
           </div>
      ) : (
          <div className="bg-green-50 border border-green-200 p-3 rounded-xl text-center text-green-800 font-bold text-sm">
              <CheckCircle className="inline-block ml-1" size={16}/> כל החובות שולמו בהצלחה!
          </div>
      )}

      {/* רשימת העבודות */}
      {debts.map((debt, idx) => {
          const isPaid = getVal(debt, 'isPaid', 'IsPaid');
          const isSubmitted = getVal(debt, 'isSubmitted', 'IsSubmitted');
          const link = getVal(debt, 'materialLink', 'MaterialLink');
          const debtId = getVal(debt, 'debtID', 'DebtID');
          const hours = getVal(debt, 'hours', 'Hours') || 0;
          
          const canSubmit = allowedSubmissionIds.has(debtId);

          return (
              <div key={idx} className={`p-4 rounded-xl shadow-sm border flex flex-col gap-3 ${canSubmit ? 'bg-white border-gray-100' : 'bg-gray-50 border-gray-200 opacity-80'}`}>
                  <div className="flex justify-between items-start">
                      <div>
                          <h4 className="font-bold text-gray-800 text-lg flex items-center gap-2">
                              {getVal(debt, 'lessonName', 'LessonName')}
                              {!canSubmit && <span className="text-xs bg-gray-200 text-gray-600 px-2 py-0.5 rounded-full">פטורה מהגשה</span>}
                          </h4>
                          <p className="text-sm text-gray-500">
                              {getVal(debt, 'lessonType', 'LessonType')} | {getVal(debt, 'lecturerName', 'LecturerName')}
                              <span className="mr-2 font-medium text-blue-600">({hours} שעות)</span>
                          </p>
                      </div>
                  </div>

                  <div className="flex gap-2 text-xs font-bold">
                      <div className={`px-3 py-1 rounded-full flex items-center gap-1 border ${isPaid ? 'bg-green-50 border-green-200 text-green-700' : 'bg-red-50 border-red-200 text-red-700'}`}>
                          {isPaid ? <CheckCircle size={12}/> : <X size={12}/>}
                          {isPaid ? 'שולם' : 'לא שולם'}
                      </div>
                      {canSubmit ? (
                           <div className={`px-3 py-1 rounded-full flex items-center gap-1 border ${isSubmitted ? 'bg-blue-50 border-blue-200 text-blue-700' : 'bg-gray-100 border-gray-200 text-gray-500'}`}>
                              {isSubmitted ? <CheckCircle size={12}/> : <Clock size={12}/>}
                              {isSubmitted ? 'הוגש' : 'ממתין להגשה'}
                          </div>
                      ) : (
                          <div className="px-3 py-1 rounded-full flex items-center gap-1 border bg-gray-200 text-gray-500 border-gray-300">
                              <Ban size={12}/> פטורה מהגשה
                          </div>
                      )}
                  </div>

                  <div className="pt-3 border-t border-gray-100 mt-1">
                      {!isPaid ? (
                          <div className="text-center text-sm text-red-500 font-medium flex justify-center items-center gap-1 py-1">
                               <Lock size={14}/> יש להסדיר תשלום כדי לפתוח
                          </div>
                      ) : (
                          canSubmit ? (
                              <div className="flex flex-col gap-2">
                                  {/* קישור לחומר - תמיד מופיע */}
                                  {link && <a href={link} target="_blank" rel="noreferrer" className="w-full bg-gray-100 hover:bg-gray-200 text-gray-700 py-2 rounded-lg text-sm font-medium flex justify-center items-center gap-2 border border-gray-200 transition"><FileText size={18}/> הורדת חומרי למידה</a>}
                                  
                                  {/* אם לא הוגש - כפתור העלאה. אם הוגש - רק הודעת הצלחה */}
                                  {!isSubmitted ? (
                                      <label className={`w-full flex justify-center items-center gap-2 py-2.5 rounded-lg text-sm font-bold cursor-pointer shadow transition ${uploadingId === debtId ? 'bg-gray-300' : 'bg-blue-600 text-white hover:bg-blue-700'}`}>
                                          {uploadingId === debtId ? <Loader2 className="animate-spin" size={18}/> : <Upload size={18}/>}
                                          {uploadingId === debtId ? "מעלה..." : "העלאת עבודה"}
                                          <input type="file" className="hidden" disabled={uploadingId === debtId} onChange={(e) => onUpload(e, debtId)} />
                                      </label>
                                  ) : (
                                      <div className="w-full bg-green-50 border border-green-200 text-green-800 py-2.5 rounded-lg text-sm font-bold flex justify-center items-center gap-2 cursor-default">
                                          <Check size={18}/> העבודה בוצעה והוגשה בהצלחה!
                                      </div>
                                  )}
                              </div>
                          ) : (
                              <div className="text-center text-sm text-gray-500 py-2 italic">
                                  קורס זה נמצא מעבר למכסת ה-300 שעות ואינו דורש הגשה.
                              </div>
                          )
                      )}
                  </div>
              </div>
          );
      })}
    </div>
  );
};

// --- האפליקציה הראשית ---
function App() {
  const [messages, setMessages] = useState([
    { role: 'bot', text: 'שלום! אני בוט ההשלמות שלך. כדי להתחיל, אנא הזיני את מספר תעודת הזהות שלך.' }
  ]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const [studentData, setStudentData] = useState(null); 
  const [paymentModal, setPaymentModal] = useState(null);
  const [uploadingId, setUploadingId] = useState(null);

  const messagesEndRef = useRef(null);
  const scrollToBottom = () => messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  useEffect(() => scrollToBottom(), [messages]);

  const handleSend = async () => {
    if (!input.trim()) return;
    const userMsg = { role: 'user', text: input };
    setMessages(prev => [...prev, userMsg]);
    setInput('');
    setLoading(true);

    try {
      const response = await axios.post('http://localhost:5219/api/chat/message', {
        studentId: studentData ? studentData.studentId : "", userMessage: userMsg.text
      });

      const data = response.data;
      
      if (data.data && Array.isArray(data.data) && data.data.length > 0) {
          const first = data.data[0];
          setStudentData({
              studentId: first.StudentID || first.studentID,
              firstName: first.FirstName || first.firstName,
              lastName: first.LastName || first.lastName,
              debts: data.data
          });
      }
      setMessages(prev => [...prev, { role: 'bot', text: data.reply, actionType: data.actionType, data: data.data }]);
    } catch (error) {
      setMessages(prev => [...prev, { role: 'bot', text: 'שגיאה בתקשורת עם השרת. אנא וודאי שהוא דולק.' }]);
    } finally {
      setLoading(false);
    }
  };

  const handleKeyPress = (e) => { if (e.key === 'Enter') handleSend(); };

  const handleFileUpload = async (event, debtId) => {
    const file = event.target.files[0];
    if (!file) return;
    setUploadingId(debtId);
    const formData = new FormData();
    formData.append('file', file);
    formData.append('debtId', debtId);
    
    try {
      await axios.post('http://localhost:5219/api/submission/upload', formData, { headers: { 'Content-Type': 'multipart/form-data' } });
      alert("הקובץ הועלה בהצלחה!");
      
      if (studentData) {
          const newDebts = studentData.debts.map(d => {
              const id = getVal(d, 'debtID', 'DebtID');
              return (id === debtId) ? { ...d, IsSubmitted: true, isSubmitted: true } : d;
          });
          setStudentData({ ...studentData, debts: newDebts });
          setMessages(prev => {
              const lastMsg = prev[prev.length - 1];
              if (lastMsg.data) return [...prev.slice(0, -1), { ...lastMsg, data: newDebts }];
              return prev;
          });
      }
    } catch (e) { alert("שגיאה בהעלאה"); } finally { setUploadingId(null); }
  };

  const handleOpenPayment = (debtsToPay, amount) => {
      const student = studentData ? {
          StudentID: studentData.studentId,
          FirstName: studentData.firstName,
          LastName: studentData.lastName
      } : {};
      setPaymentModal({ amount, student, debts: debtsToPay });
  };

  const onPaymentSuccess = async (transId) => {
    const debtsToPay = paymentModal.debts; 
    setPaymentModal(null);
    try {
        await axios.post('http://localhost:5219/api/payment/verify', {
            TransactionId: transId,
            StudentId: studentData.studentId
        });
        alert("התשלום התקבל בהצלחה!");
        
        const newDebts = studentData.debts.map(d => {
            const id = getVal(d, 'debtID', 'DebtID');
            const wasPaid = debtsToPay.some(pd => getVal(pd, 'debtID', 'DebtID') === id);
            return wasPaid ? { ...d, IsPaid: true, isPaid: true } : d;
        });

        setStudentData({ ...studentData, debts: newDebts });
        setMessages(prev => [...prev, { 
            role: 'bot', 
            text: `התשלום התקבל בהצלחה! כעת ניתן לבצע את העבודות שברשימה.`,
            data: newDebts 
        }]);
    } catch (e) { alert("שגיאה בעדכון השרת"); }
  };

  return (
    <div dir="rtl" className="h-screen flex flex-col bg-gray-50 font-sans">
      <header className="bg-gradient-to-r from-blue-800 to-blue-600 text-white p-4 font-bold flex justify-between shadow-lg sticky top-0 z-10">
        <h1 className="text-xl font-black">🎓 בוט השלמות</h1>
        <Link to="/admin" className="bg-white/20 p-2 rounded-lg hover:bg-white/30 transition"><Lock size={20}/></Link>
      </header>
      
      <div className="flex-1 overflow-y-auto p-4 space-y-4">
        {messages.map((m, i) => (
          <div key={i} className={`flex ${m.role === 'user' ? 'justify-end' : ''}`}>
            <div className={`p-4 rounded-2xl max-w-[95%] md:max-w-[80%] shadow-sm ${m.role === 'user' ? 'bg-blue-600 text-white rounded-br-none' : 'bg-white text-gray-800 rounded-bl-none border border-gray-100'}`}>
              <p className="whitespace-pre-line leading-relaxed mb-2">{m.text}</p>
              {m.data && (
                  <DebtsList 
                      debts={m.data} 
                      onPay={handleOpenPayment} 
                      onUpload={handleFileUpload} 
                      uploadingId={uploadingId}
                  />
              )}
            </div>
          </div>
        ))}
        {loading && <div className="flex"><div className="bg-gray-200 text-gray-500 p-3 rounded-2xl text-sm animate-pulse">מקליד...</div></div>}
        <div ref={messagesEndRef}/>
      </div>

      <div className="p-4 bg-white border-t flex gap-2">
        <input value={input} onChange={e=>setInput(e.target.value)} onKeyDown={e=>e.key==='Enter' && handleSend()} className="flex-1 bg-gray-100 border-0 rounded-full px-5 focus:ring-2 focus:ring-blue-500" placeholder="הקלדי הודעה..." disabled={loading} />
        <button onClick={handleSend} className="bg-blue-600 text-white p-3 rounded-full hover:bg-blue-700 transition"><Send size={20}/></button>
      </div>

      {paymentModal && (
        <PaymentIframe 
            totalAmount={paymentModal.amount} 
            student={paymentModal.student}
            onSuccess={onPaymentSuccess}
            onClose={() => setPaymentModal(null)}
        />
      )}
    </div>
  );
}

export default App;