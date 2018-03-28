﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.IO;
using System.Security.Cryptography;
using System.Drawing;
using HslCommunication.BasicFramework;
using HslCommunication.LogNet;
using HslCommunication.Core;

namespace HslCommunication.Enthernet
{
    /// <summary>
    /// 文件管理类服务器，负责服务器所有分类文件的管理，特点是不支持文件附加数据，但是支持直接访问文件名
    /// </summary>
    public class AdvancedFileServer : HslCommunication.Core.Net.NetworkFileServerBase
    {

        #region Constructor

        /// <summary>
        /// 实例化一个对象
        /// </summary>
        public AdvancedFileServer( )
        {
            
        }

        #endregion

        #region Override Method

        /// <summary>
        /// 处理数据
        /// </summary>
        /// <param name="obj"></param>
        protected override void ThreadPoolLogin( object obj )
        {
            if (obj is Socket socket)
            {

                OperateResult result = new OperateResult( );

                // 获取ip地址
                string IpAddress = ((IPEndPoint)(socket.RemoteEndPoint)).Address.ToString( );

                // 接收操作信息
                if (!ReceiveInformationHead(
                    socket,
                    out int customer,
                    out string fileName,
                    out string Factory,
                    out string Group,
                    out string Identify).IsSuccess)
                {
                    return;
                }

                string relativeName = ReturnRelativeFileName( Factory, Group, Identify, fileName );

                // 操作分流

                if (customer == HslProtocol.ProtocolFileDownload)
                {
                    string fullFileName = ReturnAbsoluteFileName( Factory, Group, Identify, fileName );

                    // 发送文件数据
                    if (!SendFileAndCheckReceive( socket, fullFileName, fileName, "", "").IsSuccess)
                    {
                        LogNet?.WriteError( ToString(), $"{StringResources.FileDownloadFailed}:{relativeName} ip:{IpAddress}" );
                        return;
                    }
                    else
                    {
                        LogNet?.WriteInfo( ToString( ), StringResources.FileDownloadSuccess + ":" + relativeName );
                    }
                    socket?.Close( );
                }
                else if (customer == HslProtocol.ProtocolFileUpload)
                {
                    string tempFileName = FilesDirectoryPathTemp + "\\" + CreateRandomFileName( );

                    string fullFileName = ReturnAbsoluteFileName( Factory, Group, Identify, fileName );

                    // 上传文件
                    CheckFolderAndCreate( );

                    try
                    {
                        FileInfo info = new FileInfo( fullFileName );
                        if (!Directory.Exists( info.DirectoryName ))
                        {
                            Directory.CreateDirectory( info.DirectoryName );
                        }
                    }
                    catch (Exception ex)
                    {
                        LogNet?.WriteException( ToString( ), "创建文件夹失败：" + fullFileName, ex );
                        socket?.Close( );
                        return;
                    }

                    if (ReceiveFileFromSocketAndMoveFile(
                        socket,                                 // 网络套接字
                        tempFileName,                           // 临时保存文件路径
                        fullFileName,                           // 最终保存文件路径
                        out string FileName,                    // 文件名称，从客户端上传到服务器时，为上传人
                        out long FileSize,
                        out string FileTag,
                        out string FileUpload
                        ).IsSuccess)
                    {
                        socket?.Close( );
                        LogNet?.WriteInfo( ToString( ), StringResources.FileUploadSuccess + ":" + relativeName );
                    }
                    else
                    {
                        LogNet?.WriteInfo( ToString( ), StringResources.FileUploadFailed + ":" + relativeName );
                    }
                }
                else if (customer == HslProtocol.ProtocolFileDelete)
                {
                    string fullFileName = ReturnAbsoluteFileName( Factory, Group, Identify, fileName );

                    bool deleteResult = DeleteFileByName( fullFileName );

                    // 回发消息
                    if (SendStringAndCheckReceive(
                        socket,                                                                // 网络套接字
                        deleteResult ? 1 : 0,                                                  // 是否移动成功
                        deleteResult ? "成功" : "失败"                                        // 字符串数据
                        ).IsSuccess)
                    {
                        socket?.Close( );
                    }

                    if (deleteResult) LogNet?.WriteInfo( ToString( ), StringResources.FileDeleteSuccess + ":" + fullFileName );
                }
                else if (customer == HslProtocol.ProtocolFileDirectoryFiles)
                {
                    List<GroupFileItem> fileNames = new List<GroupFileItem>( );
                    foreach (var m in GetDirectoryFiles( Factory, Group, Identify ))
                    {
                        FileInfo fileInfo = new FileInfo( m );
                        fileNames.Add( new GroupFileItem( )
                        {
                            FileName = fileInfo.Name,
                            FileSize = fileInfo.Length,
                        } );
                    }

                    Newtonsoft.Json.Linq.JArray jArray = Newtonsoft.Json.Linq.JArray.FromObject( fileNames.ToArray( ) );
                    if (SendStringAndCheckReceive(
                        socket,
                        HslProtocol.ProtocolFileDirectoryFiles,
                        jArray.ToString( )).IsSuccess)
                    {
                        socket?.Close( );
                    }
                }
                else if (customer == HslProtocol.ProtocolFileDirectories)
                {
                    List<string> folders = new List<string>( );
                    foreach (var m in GetDirectories( Factory, Group, Identify ))
                    {
                        DirectoryInfo directory = new DirectoryInfo( m );
                        folders.Add( directory.Name );
                    }

                    Newtonsoft.Json.Linq.JArray jArray = Newtonsoft.Json.Linq.JArray.FromObject( folders.ToArray( ) );
                    if (SendStringAndCheckReceive(
                        socket,
                        HslProtocol.ProtocolFileDirectoryFiles,
                        jArray.ToString( ) ).IsSuccess)
                    {
                        socket?.Close( );
                    }
                }
                else
                {
                    socket?.Close( );
                }
            }
        }

        /// <summary>
        /// 初始化数据
        /// </summary>
        protected override void StartInitialization( )
        {
            if (string.IsNullOrEmpty( FilesDirectoryPathTemp ))
            {
                throw new ArgumentNullException( "FilesDirectoryPathTemp", "No saved path is specified" );
            }

            base.StartInitialization( );
        }

        /// <summary>
        /// 检查文件夹
        /// </summary>
        protected override void CheckFolderAndCreate( )
        {
            if (!Directory.Exists( FilesDirectoryPathTemp ))
            {
                Directory.CreateDirectory( FilesDirectoryPathTemp );
            }

            base.CheckFolderAndCreate( );
        }

        /// <summary>
        /// 从网络套接字接收文件并移动到目标的文件夹中，如果结果异常，则结束通讯
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="savename"></param>
        /// <param name="fileNameNew"></param>
        /// <param name="filename"></param>
        /// <param name="size"></param>
        /// <param name="filetag"></param>
        /// <param name="fileupload"></param>
        /// <returns></returns>
        private OperateResult ReceiveFileFromSocketAndMoveFile(
            Socket socket,
            string savename,
            string fileNameNew,
            out string filename,
            out long size,
            out string filetag,
            out string fileupload
            )
        {
            // 先接收文件
            OperateResult<FileBaseInfo> fileInfo = ReceiveFileFromSocket( socket, savename, null );
            filename = fileInfo.Content.Name;
            size = fileInfo.Content.Size;
            filetag = fileInfo.Content.Tag;
            fileupload = fileInfo.Content.Upload;

            if (!fileInfo.IsSuccess)                          
            {
                DeleteFileByName( savename );
                return fileInfo;
            }


            // 标记移动文件，失败尝试三次
            int customer = 0;
            int times = 0;
            while (times < 3)
            {
                times++;
                if (MoveFileToNewFile( savename, fileNameNew ))
                {
                    customer = 1;
                    break;
                }
                else
                {
                    Thread.Sleep( 500 );
                }
            }
            if (customer == 0)
            {
                DeleteFileByName( savename );
            }

            // 回发消息
            return SendStringAndCheckReceive( socket, customer, "成功" );
        }

        #endregion

        #region Public Method

        /// <summary>
        /// 用于接收上传文件时的临时文件夹，临时文件使用结束后会被删除
        /// </summary>
        public string FilesDirectoryPathTemp
        {
            get { return m_FilesDirectoryPathTemp; }
            set { m_FilesDirectoryPathTemp = PreprocessFolderName( value ); }
        }

        #endregion

        #region Private Member

        private string m_FilesDirectoryPathTemp = null;

        #endregion

    }
}
