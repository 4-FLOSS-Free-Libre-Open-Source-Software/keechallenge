﻿/* KeeChallenge--Provides Yubikey challenge-response capability to Keepass
*  Copyright (C) 2014  Ben Rush
*  
*  This program is free software; you can redistribute it and/or
*  modify it under the terms of the GNU General Public License
*  as published by the Free Software Foundation; either version 2
*  of the License, or (at your option) any later version.
*  
*  This program is distributed in the hope that it will be useful,
*  but WITHOUT ANY WARRANTY; without even the implied warranty of
*  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
*  GNU General Public License for more details.
*  
*  You should have received a copy of the GNU General Public License
*  along with this program; if not, write to the Free Software
*  Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
*/

using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Xml;

using KeePassLib.Keys;
using KeePassLib.Utility;
using KeePassLib.Cryptography;
using KeePass.UI;


namespace KeeChallenge
{
    public sealed class KeeChallengeProv : KeyProvider
    {
        public const string m_name = "Yubikey challenge-response";
        public const int keyLenBytes = 20;
        public const int challengeLenBytes = 64;
        public const int responseLenBytes = 20;
        public const int secretLenBytes = 20;

        public KeeChallengeProv()
        {
        }

        private string AuxFile
        {
            get;
            set;
        }        

        public override string Name
        {
            get { return m_name; }
        }

        public override bool SecureDesktopCompatible
        {
            get
            {
                return true;
            }
        }

        public override byte[] GetKey(KeyProviderQueryContext ctx)
        {
            if (ctx == null)
            {
                Debug.Assert(false);
                return null;
            }
            
            string db = ctx.DatabasePath;
            AuxFile = Path.Combine(Path.GetDirectoryName(db), Path.GetFileNameWithoutExtension(db) + ".xml");

            try
            {
                if (ctx.CreatingNewKey) return Create(ctx);
                return Get(ctx);
            }
            catch (Exception ex) { MessageService.ShowWarning(ex.Message); }

            return null;
        }

        public static byte[] GenerateChallenge()
        {
            CryptoRandom rand = CryptoRandom.Instance;
            return CryptoRandom.Instance.GetRandomBytes(challengeLenBytes);            
        }

        public static byte[] GenerateResponse(byte[] challenge, byte[] key)
        {
            HMACSHA1 hmac = new HMACSHA1(key);
            byte[] resp = hmac.ComputeHash(challenge);
            hmac.Clear();
            return resp;
        }

        private static bool EncryptAndSave(byte[] secret, string file)
        {
            //generate a random challenge for use next time
            byte[] challenge = GenerateChallenge();

            //generate the expected HMAC-SHA1 response for the challenge based on the secret
            byte[] resp = GenerateResponse(challenge, secret);

            //use the response to encrypt the secret
            SHA256 sha = SHA256Managed.Create();
            byte[] key = sha.ComputeHash(resp); // get a 256 bit key from the 160 bit hmac response
            byte[] secretHash = sha.ComputeHash(secret);

            AesManaged aes = new AesManaged();
            aes.KeySize = key.Length * sizeof(byte) * 8; //pedantic, but foolproof
            aes.Key = key;
            aes.GenerateIV();
            aes.Padding = PaddingMode.PKCS7;
            byte[] iv = aes.IV;

            byte[] encrypted;
            ICryptoTransform enc = aes.CreateEncryptor();
            using (MemoryStream msEncrypt = new MemoryStream())
            {
                using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, enc, CryptoStreamMode.Write))
                {
                    csEncrypt.Write(secret, 0, secret.Length);
                    csEncrypt.FlushFinalBlock();

                    encrypted = msEncrypt.ToArray();
                    csEncrypt.Close();
                    csEncrypt.Clear();
                }
                msEncrypt.Close();
            }

            sha.Clear();
            aes.Clear();

            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception)
            {
                MessageService.ShowWarning(String.Format("Error: unable to delete old file {0}. Check directory perissions", file));
                return false;
            }

            try
            {
                XmlWriterSettings settings = new XmlWriterSettings();
                settings.CloseOutput = true;
                settings.Indent = true;
                settings.IndentChars = "\t";
                settings.NewLineOnAttributes = true;                

                XmlWriter xml = XmlWriter.Create(file,settings);
                xml.WriteStartDocument();
                xml.WriteStartElement("data");

                xml.WriteStartElement("aes");
                xml.WriteElementString("encrypted", Convert.ToBase64String(encrypted));
                xml.WriteElementString("iv", Convert.ToBase64String(iv));
                xml.WriteEndElement();

                xml.WriteElementString("challenge", Convert.ToBase64String(challenge));
                xml.WriteElementString("verification", Convert.ToBase64String(secretHash));

                xml.WriteEndElement();
                xml.WriteEndDocument();
                xml.Close();                
            }
            catch (Exception)
            {
                MessageService.ShowWarning(String.Format("Error: unable to write to file {0}. Is the file in use by another process?", file));
                return false;
            }
          
            return true;
        }

        private static bool DecryptSecret(byte[] encryptedSecret, byte[] yubiResp, byte[] iv, byte[] verification, out byte[] secret)
        {
            //use the response to decrypt the secret
            SHA256 sha = SHA256Managed.Create();
            byte[] key = sha.ComputeHash(yubiResp); // get a 256 bit key from the 160 bit hmac response

            AesManaged aes = new AesManaged();
            aes.KeySize = key.Length * sizeof(byte) * 8; //pedantic, but foolproof
            aes.Key = key;
            aes.IV = iv;
            aes.Padding = PaddingMode.PKCS7;

            secret = new byte[keyLenBytes];
            ICryptoTransform dec = aes.CreateDecryptor();
            using (MemoryStream msDecrypt = new MemoryStream(encryptedSecret))
            {
                using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, dec, CryptoStreamMode.Read))
                {
                    csDecrypt.Read(secret, 0, secret.Length);
                    csDecrypt.Close();
                    csDecrypt.Clear();
                }
                msDecrypt.Close();
            }

            byte[] secretHash = sha.ComputeHash(secret);
            for (int i = 0; i < secretHash.Length; i++)
            {
                if (secretHash[i] != verification[i])
                {
                    MessageService.ShowWarning("Incorrect response!");
                    Array.Clear(secret, 0, secret.Length);
                    return false;
                }
            }

            //return the secret
            sha.Clear();
            aes.Clear();
            return true;
        }
       
        private static bool ReadEncryptedSecret(string file, out byte[] encryptedSecret, out byte[] challenge, out byte[] iv, out byte[] verification)
        {
            encryptedSecret = null;
            iv = null;
            challenge = null;
            verification = null;

            //read file
            XmlReader xml;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings();
                settings.CloseInput = true;                
                xml = XmlReader.Create(file,settings);
            }
            catch (Exception) 
            {
                MessageService.ShowWarning(String.Format("Error: unable to read file {0}. Reverting to recovery mode.", file)); 
                return false;
            }

            try
            {
                while (xml.Read())
                {
                    if (xml.IsStartElement())
                    {
                        switch (xml.Name)
                        {
                            case "encrypted":
                                xml.Read();
                                encryptedSecret = Convert.FromBase64String(xml.Value.Trim());
                                break;
                            case "iv":
                                xml.Read();
                                iv = Convert.FromBase64String(xml.Value.Trim());
                                break;
                            case "challenge":
                                xml.Read();
                                challenge = Convert.FromBase64String(xml.Value.Trim());
                                break;
                            case "verification":
                                xml.Read();
                                verification = Convert.FromBase64String(xml.Value.Trim());
                                break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                MessageService.ShowWarning(String.Format("Error: file {0} could not be read correctly. Is the file corrupt? Reverting to recovery mode", file));
                return false;
            }
            
            xml.Close();
            //if failed, return false
            return true;
        }

        private byte[] Create(KeyProviderQueryContext ctx)
        {
            //show the entry dialog for the secret
            //get the secret
            KeyCreation creator = new KeyCreation();
            if (creator.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;

            byte[] secret = new byte[creator.Secret.Length];
            
            Array.Copy(creator.Secret, secret, creator.Secret.Length); //probably paranoid here, but not a big performance hit
            Array.Clear(creator.Secret, 0, creator.Secret.Length);

            if (!EncryptAndSave(secret,AuxFile))
            {
                MessageService.ShowWarning(String.Format("Error: unable to save file {0}. No key was created.", AuxFile));
                return null;
            }

            //store the encrypted secret, the iv, and the challenge to disk           
           
            return secret;
        }

        private byte[] Get(KeyProviderQueryContext ctx)
        {
            //read the challenge, iv, and encrypted secret from disk -- if missing, you must use recovery mode
            byte[] encryptedSecret = null;
            byte[] iv = null;
            byte[] challenge = null;
            byte[] verification = null;
            byte[] secret = null;

            if (!ReadEncryptedSecret(AuxFile, out encryptedSecret, out challenge, out iv, out verification))
            {                              
                secret = RecoveryMode();
                EncryptAndSave(secret, AuxFile);
                return secret;
            }
                //show the dialog box prompting user to press yubikey button
            byte[] resp = new byte[responseLenBytes];
            KeyEntry entryForm = new KeyEntry();
            entryForm.Challenge = challenge;
            
            if (entryForm.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return null;

            entryForm.Response.CopyTo(resp,0);
            Array.Clear(entryForm.Response,0,entryForm.Response.Length);

            if (DecryptSecret(encryptedSecret, resp, iv, verification, out secret))
            {
                if (EncryptAndSave(secret,AuxFile))
                    return secret;
                else return null;
            }
            else
            {
                return null;
            }
        }

        private static byte[] RecoveryMode()
        {
            //prompt user to enter secret
            RecoveryMode recovery = new RecoveryMode();
            if (recovery.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
            byte[] secret = new byte[recovery.Secret.Length];

            recovery.Secret.CopyTo(secret, 0);
            Array.Clear(recovery.Secret, 0, recovery.Secret.Length);            
             
            return secret;
       }

    }
}