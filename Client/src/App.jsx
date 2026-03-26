
import { useState, useRef, useEffect ,useMemo } from 'react';
import axios from 'axios';
import { Send, Upload, FileText, CheckCircle, AlertCircle, Loader2, Lock, X, Check, Clock, Ban, Book, ExternalLink, Download, CreditCard, User, MapPin, Phone, Mail} from 'lucide-react';
import { Link } from 'react-router-dom';

// --- פונקציות עזר ---
const getVal = (obj, key1, key2) => {
  if (!obj) return null;
  return obj[key1] !== undefined ? obj[key1] : obj[key2];
};

// בראש הקובץ App.js
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || "http://localhost:5219";

const PaymentIframe = ({ totalAmount, onSuccess, onClose, debtsToPay, student }) => {
  const iframeRef = useRef(null);
  const [status, setStatus] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [tashlumim, setTashlumim] = useState('1');

  const knownZeout = useMemo(() => {
    // 👇👇👇 שורת הבדיקה החדשה 👇👇👇
    console.log("🔥 בדיקת נתונים בזמן אמת:", JSON.stringify(debtsToPay[0], null, 2));
    
    if (!debtsToPay || debtsToPay.length === 0) return '';
    
    const debt = debtsToPay[0];
    return debt.StudentID || debt.studentID || debt.studentId || debt.Zeout || ''; 
  }, [debtsToPay]);

  const [formData, setFormData] = useState({
    firstName: '',
    lastName: '',
    zeout: '', // יתעדכן ב-useEffect למטה
    street: '',
    city: '',
    phone: '',
    email: ''
  });

  // 2. עדכון הטופס אוטומטית ברגע שהרכיב נטען עם הת.ז הידועה
  useEffect(() => {
    if (knownZeout) {
      setFormData(prev => ({ ...prev, zeout: knownZeout }));
    }
  }, [knownZeout]);

  const handleChange = (e) => {
    setFormData({ ...formData, [e.target.name]: e.target.value });
  };

  const currentOrderId = useMemo(() => {
    return Math.floor(Math.random() * 90000000 + 10000000).toString();
  }, []);

  const cleanAmount = useMemo(() => {
    if (!totalAmount) return '0';
    return totalAmount.toString().replace(/[^\d.]/g, '');
  }, [totalAmount]);

  const maxTashlumim = useMemo(() => {
    return parseFloat(cleanAmount) > 250 ? 5 : 1;
  }, [cleanAmount]);

  const param1Value = useMemo(() => {
    if (!debtsToPay || debtsToPay.length === 0) return '';
    const debtIds = debtsToPay
      .map(d => d.DebtID || d.debtID || d.id)
      .filter(id => id)
      .join(',');
    return debtIds;
  }, [debtsToPay]);

  const handlePayment = () => {
    console.log("🔵 Starting payment process");

    if (!formData.firstName || !formData.zeout || !formData.phone) {
      setStatus('שגיאה: נא למלא את כל שדות החובה (שם, ת"ז, טלפון)');
      return;
    }

    if (formData.zeout.length !== 9) {
      setStatus('שגיאה: תעודת זהות חייבת להיות 9 ספרות');
      return;
    }

    // ✅ בדיקה חשובה: אנחנו צריכים DebtIDs כדי עדכן את DB אחרי התשלום!
    console.log("📋 Param1Value:", param1Value);
    console.log("📊 debtsToPay:", JSON.stringify(debtsToPay, null, 2));
    
    if (!param1Value || param1Value.trim() === '') {
      setStatus('שגיאה: לא נמצאו קורסים לתשלום. אנא רענן את העמוד ונסה שוב.');
      return;
    }

    setIsSubmitting(true);
    setStatus('מעבד תשלום, אנא המתן/י...');

    const dataToNedarim = {
      Name: 'FinishTransaction2',
      Value: {
        Mosad: '7001475',
        ApiValid: 'MykxduB97f',
        Zeout: formData.zeout || '',
        FirstName: formData.firstName || '',
        LastName: formData.lastName || '',
        Street: formData.street || '',
        City: formData.city || '',
        Phone: formData.phone || '',
        Mail: formData.email || '',
        PaymentType: 'Ragil',
        Amount: cleanAmount,
        Tashlumim: tashlumim,
        Currency: '1',
        Groupe: 'תשלום עבור השלמת עבודות'|| '',
        Comment: 'Payment',
        Param1: param1Value || '',
        Param2: currentOrderId || '',
        CallBack: 'https://auto-office.byta.org.il/api/Payment/callback',
        CallBackMailError: '',
      }
    };

    console.log("🚀 Sending to Nedarim iframe:", JSON.stringify(dataToNedarim, null, 2));
    console.log("✅ Param1 (DebtIDs):", param1Value);
    console.log("✅ CallBack URL:", 'https://auto-office.byta.org.il/api/Payment/callback');

    if (iframeRef.current && iframeRef.current.contentWindow) {
      iframeRef.current.contentWindow.postMessage(dataToNedarim, '*');
    } else {
      setStatus('שגיאה: האייפרם לא נטען כראוי');
      setIsSubmitting(false);
    }
  };

  useEffect(() => {
    const handleMessage = (event) => {
      // ... (שאר הקוד של ה-Listener נשאר זהה למה ששלחת) ...
      let data = event.data;
      if (typeof data === 'string') {
        try { data = JSON.parse(data); } catch (e) { return; }
      }
      if (!data || !data.Name) return;

      if (data.Name === 'Height' && iframeRef.current) {
         iframeRef.current.style.height = (parseInt(data.Value) + 20) + 'px';
      }

      if (data.Name === 'TransactionResponse') {
        const val = data.Value;
        setIsSubmitting(false);
        if (val.Status === 'OK') {
          setStatus('✅ התשלום בוצע בהצלחה!');
          const txId = val.ID || val.TransactionId;
          if (onSuccess) setTimeout(() => onSuccess(txId), 1500);
        } else {
          const errorMsg = val.Message || val.ErrorMessage || 'העסקה נכשלה';
          setStatus('❌ שגיאה: ' + errorMsg);
        }
      }

      if (data.Name === 'Error') {
        setStatus('שגיאה: ' + (data.Value || 'שגיאה לא ידועה'));
        setIsSubmitting(false);
      }
    };

    window.addEventListener('message', handleMessage);
    return () => window.removeEventListener('message', handleMessage);
  }, [onSuccess]);

  // בדיקת סביבת פיתוח (localhost או 127.0.0.1)
  const isDev = typeof window !== 'undefined' && (window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1');

  return (
    <div className="fixed inset-0 bg-black/70 backdrop-blur-sm flex items-center justify-center z-[9999] p-4" style={{ direction: 'rtl' }}>
      <div className="bg-white w-full max-w-3xl rounded-xl shadow-2xl flex flex-col max-h-[95vh] overflow-hidden">
        
        <div className="bg-[#008f78] p-4 flex justify-between items-center text-white shrink-0">
          <div>
            <h3 className="font-bold text-lg">תשלום מאובטח</h3>
            <p className="text-sm opacity-90">סכום לתשלום: <b>{totalAmount} ₪</b></p>
          </div>
          <button 
            onClick={onClose} 
            className="bg-white/10 p-2 hover:bg-white/20 rounded-full transition"
            disabled={isSubmitting}
          >
            <X size={20} />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto p-6 bg-gray-50">
          {/* כפתור דילוג על תשלום - רק בפיתוח */}
          {isDev && (
            <button
              onClick={async () => {
                setIsSubmitting(true);
                setStatus('מעדכן את מסד הנתונים...');
                try {
                  const studentId = student?.StudentID || student?.studentID;
                  // עדכן את כל הקורסים שצריכים תשלום (debtsToPay הם הקורסים שנשלחו לתשלום)
                  const debtIdsList = debtsToPay
                    .map(d => d.DebtID || d.debtID || d.id)
                    .filter(id => id)
                    .join(',');
                  
                  console.log('🔧 Dev-bypass: student object:', JSON.stringify(student, null, 2));
                  console.log('🔧 Dev-bypass: studentId:', studentId);
                  console.log('🔧 Dev-bypass: debtsToPay:', JSON.stringify(debtsToPay, null, 2));
                  console.log('🔧 Dev-bypass: DebtIds:', debtIdsList);
                  
                  // ✅ בדיקה: אם אין studentId או debtIds, אל תשלח בקשה
                  if (!studentId) {
                    setStatus('❌ שגיאה: לא נמצא מספר זהות של התלמידה');
                    setIsSubmitting(false);
                    return;
                  }
                  
                  if (!debtIdsList) {
                    setStatus('❌ שגיאה: לא נמצאו קורסים לעדכון');
                    setIsSubmitting(false);
                    return;
                  }
                  
                  const response = await axios.post(
                    `${API_BASE_URL}/api/payment/dev-mark-paid?studentId=${encodeURIComponent(studentId)}&debtIds=${encodeURIComponent(debtIdsList)}`
                  );
                  
                  console.log('✅ Dev-bypass response:', response.data);
                  
                  if (response.data.success) {
                    console.log('✅ Dev-bypass: DB updated successfully', response.data);
                    setStatus('✅ מסד הנתונים עודכן בהצלחה!');
                    if (onSuccess) setTimeout(() => onSuccess('dev-bypass-' + Date.now()), 1000);
                  } else {
                    setStatus('❌ שגיאה בעדכון מסד הנתונים');
                  }
                } catch (error) {
                  console.error('❌ Dev-bypass error:', error);
                  console.error('❌ Error response:', error.response?.data);
                  setStatus('❌ שגיאה: ' + (error.response?.data?.error || error.message));
                } finally {
                  setIsSubmitting(false);
                }
              }}
              className="mb-4 w-full py-2 rounded-lg bg-yellow-400 hover:bg-yellow-500 text-black font-bold text-lg shadow border border-yellow-600 transition-all"
              disabled={isSubmitting}
            >
              דלג על תשלום (פיתוח)
            </button>
          )}

          <div className="mb-6 bg-white p-4 rounded-lg shadow-sm border border-gray-100">
            <h4 className="font-bold text-gray-700 mb-4 flex items-center gap-2">
              <User size={18} /> פרטים אישיים
            </h4>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  שם פרטי <span className="text-red-500">*</span>
                </label>
                <input 
                  name="firstName" 
                  value={formData.firstName} 
                  onChange={handleChange} 
                  className="w-full border rounded p-2 focus:ring-2 focus:ring-[#008f78] outline-none" 
                  required 
                  disabled={isSubmitting}
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">שם משפחה</label>
                <input 
                  name="lastName" 
                  value={formData.lastName} 
                  onChange={handleChange} 
                  className="w-full border rounded p-2 focus:ring-2 focus:ring-[#008f78] outline-none" 
                  disabled={isSubmitting}
                />
              </div>
              {/* --- השינוי החשוב בשדה תעודת זהות --- */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  תעודת זהות <span className="text-red-500">* (לא ניתן לשינוי)</span>
                </label>
                <input 
                  name="zeout" 
                  value={formData.zeout} 
                  // אין onChange כי זה לקריאה בלבד
                  readOnly={true} 
                  className="w-full border rounded p-2 bg-gray-200 text-gray-600 cursor-not-allowed outline-none" 
                  required 
                  disabled={isSubmitting} // עדיין משאירים את זה
                />
              </div>
              {/* --------------------------------------- */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  טלפון <span className="text-red-500">*</span>
                </label>
                <input 
                  name="phone" 
                  value={formData.phone} 
                  onChange={handleChange} 
                  className="w-full border rounded p-2 focus:ring-2 focus:ring-[#008f78] outline-none" 
                  required 
                  disabled={isSubmitting}
                  placeholder="05xxxxxxxx"
                />
              </div>
              {maxTashlumim > 1 && (
                <div>
                   {/* ... (שאר קוד התשלומים נשאר אותו דבר) */}
                   <label className="block text-sm font-medium text-gray-700 mb-1">מספר תשלומים</label>
                   <select 
                     value={tashlumim} 
                     onChange={(e) => setTashlumim(e.target.value)}
                     className="w-full border rounded p-2 focus:ring-2 focus:ring-[#008f78] outline-none bg-white"
                     disabled={isSubmitting}
                   >
                     {[...Array(maxTashlumim)].map((_, i) => (
                       <option key={i + 1} value={i + 1}>
                         {i + 1} {i === 0 ? 'תשלום' : 'תשלומים'}
                       </option>
                     ))}
                   </select>
                </div>
              )}
            </div>
            <div className="mt-4 grid grid-cols-1 md:grid-cols-2 gap-4">
               {/* ... (שאר השדות: עיר, רחוב, אימייל - ללא שינוי) ... */}
               <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">עיר</label>
                <input name="city" value={formData.city} onChange={handleChange} className="w-full border rounded p-2 focus:ring-2 focus:ring-[#008f78] outline-none" disabled={isSubmitting} />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">רחוב</label>
                <input name="street" value={formData.street} onChange={handleChange} className="w-full border rounded p-2 focus:ring-2 focus:ring-[#008f78] outline-none" disabled={isSubmitting} />
              </div>
              <div className="md:col-span-2">
                <label className="block text-sm font-medium text-gray-700 mb-1">אימייל</label>
                <input name="email" type="email" value={formData.email} onChange={handleChange} className="w-full border rounded p-2 focus:ring-2 focus:ring-[#008f78] outline-none" disabled={isSubmitting} />
              </div>
            </div>
          </div>
          <div className="bg-white p-4 rounded-lg shadow-sm border border-gray-100 mb-4">
             {/* ... (האייפרם נשאר אותו דבר) ... */}
             <h4 className="font-bold text-gray-700 mb-2 flex items-center gap-2">
               <CreditCard size={18} /> פרטי אשראי
             </h4>
             <iframe 
               ref={iframeRef}
               src="https://www.matara.pro/nedarimplus/iframe/?Mosad=7001475&Language=he&WaitFrame=1" 
               className="w-full border-none"
               style={{ height: '260px', width: '100%' }}
               title="Credit Card Frame"
               allow="payment; fullscreen"
             />
          </div>
          {status && (
            <div className={`p-3 rounded mb-4 text-center font-bold ${
              status.includes('הצלחה') ? 'bg-green-100 text-green-700' : 
              status.includes('שגיאה') ? 'bg-red-50 text-red-700' : 
              'bg-blue-50 text-blue-700'
            }`}>
              {status}
            </div>
          )}
          <button 
            onClick={handlePayment}
            disabled={isSubmitting}
            className={`w-full py-3 rounded-lg text-white font-bold text-lg shadow-lg transition-all ${
              isSubmitting 
                ? 'bg-gray-400 cursor-not-allowed' 
                : 'bg-[#008f78] hover:bg-[#007f6a] active:scale-[0.98]'
            }`}
          >
            {isSubmitting ? 'מעבד תשלום...' : `בצע תשלום על סך ${totalAmount} ₪`}
          </button>
        </div>
      </div>
    </div>
  );
};

/// --- רכיב 2: רשימת החובות (גרסה מתוקנת וסופית) ---
const DebtsList = ({ debts, onPay, onUpload, uploadingId }) => {
  
  // 1. יצירת רשימה ממוינת לפי DebtID
  const sortedDebts = [...debts].sort((a, b) => {
    const idA = parseInt(getVal(a, 'debtID', 'DebtID')) || 0;
    const idB = parseInt(getVal(b, 'debtID', 'DebtID')) || 0;
    return idA - idB;
  });

  // 2. סינון הקורסים להצגה - כל קורס שמאושר להגשה לפי חישוב מכסת 300 השעות
  const visibleDebts = sortedDebts.filter(debt => {
    const isAllowedFromServer = getVal(debt, 'isAllowedSubmission', 'IsAllowedSubmission');
    return isAllowedFromServer === true;
  });
  
  console.log("🔎 DebtsList Debug:");
  console.log("  sortedDebts:", sortedDebts.length);
  console.log("  visibleDebts:", visibleDebts.length);
  sortedDebts.forEach((d, i) => {
    const isPaid = getVal(d, 'isPaid', 'IsPaid');
    const isExempt = getVal(d, 'isExempt', 'IsExempt');
    console.log(`  [${i}] ${getVal(d, 'lessonName', 'LessonName')} - isPaid: ${isPaid}, isExempt: ${isExempt}`);
  });

  // 3. בדיקה האם יש חובות שטרם שולמו - תשלום הוא עבור כל הקורסים
  const unpaidDebts = sortedDebts.filter(d => !getVal(d, 'isPaid', 'IsPaid'));

  // --- תצוגת "נעול" אם יש חובות שלא שולמו ---
  if (unpaidDebts.length > 0) {
    return (
      <div className="mt-4 bg-white p-4 rounded-xl shadow-sm border border-gray-100">
        <h4 className="font-bold text-gray-800 mb-3 border-b pb-2 flex items-center gap-2">
          <Lock size={18} className="text-red-500" />
          <span>הקורסים נעולים. <span className="text-green-600 font-bold">התשלום יפתח מיד</span> את האפשרות להגשה.</span>
        </h4>
        <table className="w-full text-sm text-gray-600 border-collapse">
          <thead>
            <tr className="border-b-2 border-gray-400">
              <th className="text-right py-2 px-2 font-bold text-gray-800">שם שיעור</th>
              <th className="text-left py-2 px-2 font-bold text-gray-800">סכום לתשלום</th>
            </tr>
          </thead>
          <tbody>
            {unpaidDebts.map((debt, idx) => (
              <tr key={idx} className="border-b border-gray-200 hover:bg-gray-50 transition">
                <td className="py-3 px-2">
                  <div className="flex items-center gap-2">
                    <span className="w-1.5 h-1.5 bg-gray-400 rounded-full shrink-0"></span>
                    <span>{getVal(debt, 'lessonName', 'LessonName')}</span>
                    <span className="text-gray-400 text-xs">({getVal(debt, 'lessonType', 'LessonType')})</span>
                  </div>
                </td>
                <td className="py-3 px-2 text-left">
                  <span className="font-semibold text-orange-700">{getVal(debt, 'price', 'Price') || 50} ₪</span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    );
  }

  // 5. אם הכל שולם אבל אין קורסים להציג (למשל הכל פטור)
  if (visibleDebts.length === 0) {
    return (
      <div className="mt-4 bg-green-50 p-4 rounded-xl border border-green-100 text-green-700 text-sm flex items-center gap-2">
        <CheckCircle size={18} />
        אין חובות להגשה כרגע. כל הכבוד!
      </div>
    );
  }

  // --- תצוגת הכרטיסיות (אחרי תשלום) ---
  return (
    <div className="mt-4 space-y-4">
      {visibleDebts.map((debt, idx) => {
        const isPaid = getVal(debt, 'isPaid', 'IsPaid');
        const isSubmitted = getVal(debt, 'isSubmitted', 'IsSubmitted');
        const link = (getVal(debt, 'materialLink', 'MaterialLink') || '').trim();
        const debtId = getVal(debt, 'debtID', 'DebtID');
        const hours = getVal(debt, 'hours', 'Hours') || 0;
        const type = getVal(debt, 'lessonType', 'LessonType') || '';
        
        const isInstructionsOnly = getVal(debt, 'isInstructionsOnly', 'IsInstructionsOnly');
        const isUrl = link.startsWith('http') || link.startsWith('www');
        const isClassroom = getVal(debt, 'displayType', 'DisplayType') === 'Classroom';

        return (
          <div key={idx} className="p-5 rounded-xl shadow-sm border bg-white border-gray-100 flex flex-col gap-3 transition-all hover:shadow-md">
            <div className="flex justify-between items-start">
              <div className="flex-1">
                <h4 className="font-bold text-gray-800 text-lg flex items-center gap-2">
                  {getVal(debt, 'lessonName', 'LessonName')}
                </h4>
                <p className="text-sm text-gray-500 mt-0.5">
                  {type} | {getVal(debt, 'lecturerName', 'LecturerName')}
                  <span className="mr-2 font-medium text-blue-600/80">({hours} שעות)</span>
                </p>
              </div>
            </div>

            {/* שורת סטטוסים */}
            <div className="flex flex-wrap gap-4 items-center text-sm mt-1">
              <div className="flex items-center gap-1.5 font-medium text-green-600">
                <CheckCircle size={16} /> שולם
              </div>
              <div className="h-4 w-[1px] bg-gray-200"></div>
              
              {isInstructionsOnly ? (
                <div className="flex items-center gap-1.5 font-medium text-orange-600">
                  <AlertCircle size={16} /> נדרש ביצוע לפי הוראות
                </div>
              ) : (
                <div className={`flex items-center gap-1.5 font-medium ${isSubmitted ? 'text-blue-600' : 'text-gray-400'}`}>
                  {isSubmitted ? <CheckCircle size={16} /> : <Clock size={16} />}
                  {isSubmitted ? 'הוגש' : 'ממתין להגשה'}
                </div>
              )}
            </div>

            {/* איזור הפעולות */}
            <div className="pt-3 border-t border-gray-50 mt-1">
              <div className="flex flex-col gap-3">
                {isInstructionsOnly ? (
                  <div className="bg-orange-50 border border-orange-100 rounded-lg p-3">
                    <p className="text-sm text-orange-900 leading-relaxed">
                      <strong>הנחיות להשלמה:</strong><br/>
                      {link}<br/>
                      <span className="mt-2 block font-bold text-orange-700">
                        * לסיום הקורס, עלייך ליצור קשר עם הגורם האחראי כפי שמופיע בהוראות.
                      </span>
                    </p>
                  </div>
                ) : isClassroom ? (
                  <div className="bg-yellow-50 border border-yellow-100 rounded-lg p-3">
                     <p className="text-sm text-yellow-900 mb-3 leading-relaxed">
                       <strong> הנחיות לביצוע קורס בקלאסרום: </strong>היכנסי לקישור, השלימי את הלמידה והמטלות בתוך ה-Classroom, ובסיום <b>העלי את הקבצים גם כאן</b> כדי שנוכל לעדכן את הציון.
                     </p>
                     <div className="flex flex-col gap-2">
                        <a href={link} target="_blank" rel="noreferrer" className="w-full py-2 rounded-lg text-sm font-bold flex justify-center items-center gap-2 transition bg-white border border-yellow-300 text-yellow-800 hover:bg-yellow-100">
                          <ExternalLink size={16} /> מעבר לקלאסרום
                        </a>
                        {!isSubmitted ? (
                          <label className={`w-full flex justify-center items-center gap-2 py-2 rounded-lg text-sm font-bold cursor-pointer transition ${uploadingId === debtId ? 'bg-gray-300' : 'bg-yellow-600 text-white hover:bg-yellow-700'}`}>
                            {uploadingId === debtId ? <Loader2 className="animate-spin" size={16} /> : <Upload size={16} />}
                            {uploadingId === debtId ? "מעלה..." : "העלאת קבצים"}
                            <input type="file" className="hidden" multiple onChange={(e) => onUpload(e.target.files, debtId)} />
                          </label>
                        ) : (
                          <div className="w-full bg-green-100 text-green-800 py-2 rounded-lg text-sm font-bold flex justify-center items-center gap-2">
                            <Check size={16} /> המטלות הוגשו
                          </div>
                        )}
                     </div>
                  </div>
                ) : isUrl ? (
                  <div className="flex flex-col gap-2">
                     <a href={link} target="_blank" rel="noreferrer" className="w-full py-2.5 rounded-lg text-sm font-medium flex justify-center items-center gap-2 border transition bg-gray-50 hover:bg-gray-100 text-gray-700 border-gray-200">
                        <Download size={18} className="text-gray-500" /> הורדת חומרי למידה
                     </a>
                     {!isSubmitted ? (
                       <label className={`w-full flex justify-center items-center gap-2 py-2.5 rounded-lg text-sm font-bold cursor-pointer shadow-sm transition ${uploadingId === debtId ? 'bg-gray-300' : 'bg-blue-600 text-white hover:bg-blue-700'}`}>
                          {uploadingId === debtId ? <Loader2 className="animate-spin" size={18} /> : <Upload size={18} />}
                          {uploadingId === debtId ? "מעלה..." : "העלאת עבודה"}
                          <input type="file" className="hidden" multiple onChange={(e) => onUpload(e.target.files, debtId)} />
                       </label>
                     ) : (
                       <div className="mt-1 w-full bg-green-50/50 border border-green-100 text-green-700 py-2 rounded-lg text-sm font-medium flex justify-center items-center gap-2">
                          <Check size={16} /> העבודה הוגשה בהצלחה
                       </div>
                     )}
                  </div>
                ) : (
                  // ברירת מחדל: קורס ללא קישור ספציפי - אפשרות העלאת קובץ
                  !isSubmitted ? (
                    <label className={`w-full flex justify-center items-center gap-2 py-2.5 rounded-lg text-sm font-bold cursor-pointer shadow-sm transition ${uploadingId === debtId ? 'bg-gray-300' : 'bg-blue-600 text-white hover:bg-blue-700'}`}>
                      {uploadingId === debtId ? <Loader2 className="animate-spin" size={18} /> : <Upload size={18} />}
                      {uploadingId === debtId ? "מעלה..." : "העלאת עבודה"}
                      <input type="file" className="hidden" multiple onChange={(e) => onUpload(e.target.files, debtId)} />
                    </label>
                  ) : (
                    <div className="mt-1 w-full bg-green-50/50 border border-green-100 text-green-700 py-2 rounded-lg text-sm font-medium flex justify-center items-center gap-2">
                      <Check size={16} /> העבודה הוגשה בהצלחה
                    </div>
                  )
                )}
              </div>
            </div>
          </div>
        );
      })}
    </div>
  );
};
// --- האפליקציה הראשית ---
function App() {
  const [isFirstInteraction, setIsFirstInteraction] = useState(true);
  const [messages, setMessages] = useState([
    { role: 'bot', text: "ברוכה הבאה למערכת ההשלמות של סמינר 'בית המורה'!\nאני המזכירה הדיגיטלית כאן לשירותך.", icon: 'book' },
    { role: 'bot', text: "כדי שנוכל להתחיל, אנא הקלידי את מספר תעודת הזהות שלך." }
  ]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const [studentData, setStudentData] = useState(null);
  const [paymentModal, setPaymentModal] = useState(null);
  const [uploadingId, setUploadingId] = useState(null);
  const [paymentProcessing, setPaymentProcessing] = useState(false);

  const messagesEndRef = useRef(null);
  const lastBotMessageRef = useRef(null);

  useEffect(() => {
    if (messages.length === 0) return;
    if (loading) { messagesEndRef.current?.scrollIntoView({ behavior: "smooth" }); return; }
    const lastMsg = messages[messages.length - 1];
    if (lastMsg.role === 'bot') {
      if (lastBotMessageRef.current) { lastBotMessageRef.current.scrollIntoView({ behavior: "smooth", block: "start" }); }
    } else {
      messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
    }
  }, [messages, loading]);

  const handleSend = async () => {
    if (!input.trim()) return;
    if (isFirstInteraction) setIsFirstInteraction(false);
    const userMsg = { role: 'user', text: input };
    setMessages(prev => [...prev, userMsg]);
    setInput('');
    setLoading(true);

    try {
      const currentId = studentData?.studentId || "";
      const response = await axios.post(`${API_BASE_URL}/api/chat/message`, { studentId: currentId, userMessage: userMsg.text });
      const data = response.data;

      let finalStudentId = data.studentId || data.StudentId;
      let fName = studentData?.firstName || data.firstName || data.FirstName || '';
      let lName = studentData?.lastName || data.lastName || data.LastName || '';
      if (data.data && Array.isArray(data.data) && data.data.length > 0) {
        const first = data.data[0];
        if (!finalStudentId) finalStudentId = first.StudentID || first.studentID;
        fName = fName || first.FirstName || first.firstName || '';
        lName = lName || first.LastName || first.lastName || '';
      }

      if (finalStudentId) {
        setStudentData(prev => ({
          studentId: finalStudentId,
          firstName: fName,
          lastName: lName,
          // שמירת החובות הקיימים אם התגובה הנוכחית לא מכילה נתונים (שיחה רגילה)
          debts: data.data ?? (prev?.debts ?? [])
        }));
      }
      setMessages(prev => [...prev, { role: 'bot', text: data.reply, actionType: data.actionType, data: data.data }]);
    } catch (error) {
      console.error(error);
      setMessages(prev => [...prev, { role: 'bot', text: 'שגיאה בתקשורת עם השרת. אנא וודאי שהוא דולק ונסה שנית.' }]);
    } finally {
      setLoading(false);
    }
  };

  const handleKeyPress = (e) => { if (e.key === 'Enter') handleSend(); };

  // --- הפונקציה המעודכנת להעלאת קבצים מרובים ---
  const handleFileUpload = async (files, debtId) => {
    if (!files || files.length === 0) return;
    
    setUploadingId(debtId);
    const formData = new FormData();
    
    // לולאה להוספת כל הקבצים
    for (let i = 0; i < files.length; i++) {
        formData.append('files', files[i]);
    }
    
    formData.append('debtId', debtId);
    // הוספת ת"ז ליתר ביטחון
    if (studentData && studentData.studentId) {
        formData.append('studentId', studentData.studentId);
    }

    try {
      await axios.post(`${API_BASE_URL}/api/submission/upload`, formData, { headers: { 'Content-Type': 'multipart/form-data' } });
      alert("הקבצים הועלו בהצלחה!");

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
    } catch (e) { alert("שגיאה בהעלאה: " + (e.response?.data?.message || "נסה שנית")); } 
    finally { setUploadingId(null); }
  };

  const handleOpenPayment = (debtsToPay, amount) => {
    const student = studentData ? { StudentID: studentData.studentId, FirstName: studentData.firstName, LastName: studentData.lastName } : {};
    setPaymentModal({ amount, student, debts: debtsToPay });
  };

  const onPaymentSuccess = async (transId) => {
    // הצגת מסך טעינה מיידית לפני כל פעולה
    setPaymentProcessing(true);

    // 1. שומרים את רשימת החובות ששולמו ופרטי התשלום לפני שסוגרים את המודל
    const debtsToPay = paymentModal.debts;
    const paymentAmount = paymentModal.amount;
    console.log("💳 תשלום בוצע! קורסים ששולמו:", debtsToPay.length);
    
    // 2. סוגרים את חלונית התשלום
    setPaymentModal(null);

    // 3. עדכון הסטטוס בתוך ה-State של הלקוח (כדי שהתצוגה תשתנה מיד)
    if (studentData) {
      console.log("📊 סהכל קורסים בנתונים:", studentData.debts.length);
      
      const newDebts = studentData.debts.map(d => {
        const id = getVal(d, 'debtID', 'DebtID');
        // אם החוב הנוכחי היה ברשימת התשלום, נסמן אותו כ"שולם"
        const wasInPayList = debtsToPay.some(pd => getVal(pd, 'debtID', 'DebtID') === id);
        return wasInPayList ? { ...d, IsPaid: true, isPaid: true } : d;
      });

      console.log("✅ קורסים אחרי עדכון:", newDebts.length);
      console.log("🔍 קורסים עם isPaid=true:", newDebts.filter(d => getVal(d, 'isPaid', 'IsPaid')).length);

      // ✅ שליחת מייל עם קבלה למזכירות - ברקע, ללא המתנה
      const receiptPayload = {
        studentId: studentData.studentId,
        studentName: `${studentData.firstName} ${studentData.lastName}`,
        firstName: studentData.firstName,
        lastName: studentData.lastName,
        amount: paymentAmount,
        debts: debtsToPay.map(d => ({
          lessonName: getVal(d, 'lessonName', 'LessonName'),
          lessonType: getVal(d, 'lessonType', 'LessonType'),
          price: getVal(d, 'price', 'Price') || 50,
          lessonNumber: getVal(d, 'lessonNumber', 'LessonNumber')
        }))
      };
      axios.post(`${API_BASE_URL}/api/payment/send-receipt`, receiptPayload)
        .then(() => console.log("✅ הקבלה נשלחה בהצלחה לתיבת המזכירות!"))
        .catch(err => console.error("⚠️ שגיאה בשליחת הקבלה:", err.message));

      // מעדכנים את נתוני התלמידה בזיכרון של הדפדפן
      setStudentData({ ...studentData, debts: newDebts });
      
      // 4. מוסיפים הודעת בוט משמחת שפותחת את אפשרות העלאת הקבצים
const successMsg = `<div style="line-height: 1.4; color: #374151;">
    <div style="color: #008f78; font-size: 1.15rem; font-weight: bold; margin-bottom: 4px;">התשלום התקבל בהצלחה!</div>
    <div style="margin-bottom: 8px;"><strong>הקורסים נפתחו להגשה.</strong> כדי להשלים את התהליך בהצלחה, עקבי אחר ההוראות בכל כרטיס קורס:</div>
    <div style="margin-right: 10px; display: flex; flex-direction: column; gap: 4px;">
      <div>🔹 <strong>מטלה עם קובץ:</strong> הורידי את חומרי הלמידה, בצעי את המשימה במחשב שלך, והעלי את הקובץ המוכן בלחיצה על <b>"העלאת עבודה"</b>.</div>
      <div>🔹 <strong>קורס בקלאסרום:</strong> היכנסי לקישור, השלימי את הלמידה והמטלות בתוך ה-Classroom, ובסיום <b>העלי את הקבצים גם כאן</b> כדי שנוכל לעדכן את הציון.</div>
      <div>🔹 <strong>הוראות בלבד:</strong> קראי את ההנחיות וצרי קשר עם הגורם האחראי המופיע בתיאור לצורך השלמת הקורס.</div>
    </div>
    <div style="margin-top: 8px; font-weight: bold;">בהצלחה רבה!</div>
  </div>`;

      setMessages(prev => [
        ...prev, 
        { 
          role: 'bot', 
          text: successMsg, 
          data: newDebts, 
          actionType: 'UploadFile'
        }
      ]);
      setPaymentProcessing(false);
    } else {
      setPaymentProcessing(false);
    }
  };
  const InputArea = ({ className = "" }) => (
    <div className={`flex gap-2 ${className}`}>
      <input value={input} onChange={e => setInput(e.target.value)} onKeyDown={e => e.key === 'Enter' && handleSend()} spellCheck="false" className="flex-1 bg-white border border-gray-300 rounded-full px-5 py-3 focus:ring-2 focus:ring-[#C77D2B] focus:border-transparent focus:outline-none transition shadow-sm text-gray-700 placeholder-gray-400" placeholder={isFirstInteraction ? "הקלידי כאן מספר ת.ז." : "הקלידי הודעה..."} disabled={loading} autoFocus />
      <button onClick={handleSend} className="bg-gradient-to-b from-[#eebb77] to-[#b06d28] text-white p-3 rounded-full hover:shadow-lg transition shadow-md hover:brightness-105 transform active:scale-95 border border-[#9c5e1f]">
        <Send size={20} className="drop-shadow-sm" />
      </button>
    </div>
  );

  return (
    <div dir="rtl" className="h-screen bg-gray-100 flex items-center justify-center font-sans p-2 sm:p-6">
      <div className="w-full max-w-3xl bg-white h-full max-h-[90vh] rounded-3xl shadow-2xl flex flex-col overflow-hidden border border-gray-200 ring-1 ring-gray-100">
        <header className="bg-gradient-to-r from-blue-700 via-blue-600 to-blue-500 text-white p-4 font-bold flex justify-between items-center shadow-md z-10">
          <div className="flex items-center gap-3">
            <img src="/logo.png" alt="לוגו בית המורה" className="h-14 w-auto object-contain drop-shadow-md bg-white/10 rounded-lg p-1" />
            <h1 className="text-xl font-bold tracking-wide text-white">מזכירה דיגיטלית, כאן בשבילך.</h1>
          </div>
          <Link to="/admin" title="כניסת הנהלה" className="bg-white/20 p-2 rounded-lg hover:bg-white/30 transition backdrop-blur-sm"><Lock size={20} /></Link>
        </header>

        <div className="flex-1 overflow-y-auto p-4 space-y-4 bg-white/50 scrollbar-thin scrollbar-thumb-gray-200 pb-24">
          {messages.map((m, i) => {
            const unpaidList = m.data ? m.data.filter(d => !getVal(d, 'isPaid', 'IsPaid')) : [];

            const totalToPay = unpaidList.reduce((sum, d) => {
              const currentPrice = getVal(d, 'price', 'Price') || 50;
              return sum + Number(currentPrice);
            }, 0);
            const isLastBotMessage = m.role === 'bot' && i === messages.length - 1;

            return (
              <div key={i} ref={isLastBotMessage ? lastBotMessageRef : null} className={`flex flex-col ${m.role === 'user' ? 'items-end' : 'items-start'} animate-in fade-in slide-in-from-bottom-2 duration-300`}>
                <div className={`p-4 rounded-2xl max-w-[90%] shadow-sm relative ${m.role === 'user' ? 'bg-blue-600 text-white rounded-br-none' : 'bg-gray-100 text-gray-800 rounded-bl-none border border-gray-200'}`}>
                  {m.icon === 'book' && <div className="absolute -top-3 -right-3 bg-white text-blue-600 p-1.5 rounded-full shadow border border-gray-100"><Book size={16} /></div>}
                  <div className="leading-relaxed break-words text-sm sm:text-base" style={{ whiteSpace: 'pre-wrap' }} dangerouslySetInnerHTML={{ __html: m.text }} />
                  
                  {m.actionType === 'ShowDebts' && unpaidList.length > 0 && (
                    <div className="bg-orange-50 border border-orange-200 p-3 rounded-xl flex flex-col gap-2 shadow-sm mt-3">
                      <div><span className="font-bold text-orange-800 text-sm">נדרש תשלום ({unpaidList.length} קורסים)</span><p className="text-xs text-orange-600">סה"כ לתשלום: {totalToPay} ₪</p></div>
                      <button onClick={() => handleOpenPayment(unpaidList, totalToPay)} className="w-full bg-orange-600 hover:bg-orange-700 text-white px-3 py-2 rounded-lg text-sm font-bold shadow flex items-center justify-center gap-2 transition"><CreditCard size={16} /> תשלום אשראי</button>
                    </div>
                  )}

                  {m.actionType === 'ShowDebts' && m.data && <DebtsList debts={m.data} onPay={handleOpenPayment} onUpload={handleFileUpload} uploadingId={uploadingId} />}

                  {m.actionType === 'UploadFile' && m.data && isLastBotMessage && <DebtsList debts={studentData?.debts || m.data} onPay={handleOpenPayment} onUpload={handleFileUpload} uploadingId={uploadingId} />}
                </div>
              </div>
            );
          })}
          {loading && <div className="flex justify-start"><div className="bg-gray-50 text-gray-400 px-4 py-3 rounded-2xl rounded-bl-none text-sm flex items-center gap-2 border border-gray-100"><Loader2 className="animate-spin" size={14} /> המזכירה מקלידה...</div></div>}
          {isFirstInteraction && <div className="mt-8 animate-in zoom-in duration-500"><InputArea className="shadow-lg rounded-full" /><p className="text-center text-xs text-gray-400 mt-2">המערכת מאובטחת ודיסקרטית</p></div>}
          <div ref={messagesEndRef} />
        </div>

        {!isFirstInteraction && <div className="p-4 bg-white border-t border-gray-100"><InputArea /></div>}
        {paymentProcessing && (
          <div className="fixed inset-0 z-[10000] bg-black/60 backdrop-blur-sm flex items-center justify-center" dir="rtl">
            <div className="bg-white rounded-2xl p-8 flex flex-col items-center gap-4 shadow-2xl max-w-xs w-full mx-4">
              <Loader2 className="animate-spin text-[#008f78]" size={44} />
              <p className="text-lg font-bold text-gray-700">מתבצע עיבוד התשלום</p>
              <p className="text-sm text-gray-500 text-center">נא המתיני לפתיחת הקורסים...</p>
            </div>
          </div>
        )}
        {paymentModal && (
  <PaymentIframe 
    totalAmount={paymentModal.amount} 
    student={paymentModal.student} 
    debtsToPay={paymentModal.debts} // הוסיפי את השורה הזו
    onClose={() => setPaymentModal(null)} 
    onSuccess={onPaymentSuccess} 
  />
)}
      </div>
    </div>
  );
}

export default App;